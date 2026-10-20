// Command testserver is a small, self-contained, read-only SSH/SFTP server for
// testing SciBridge without touching the real institutional server.
//
// It deliberately behaves like a cautious institutional host:
//   - password authentication only, single fixed account
//   - a persistent host key, so the fingerprint a client pinned stays valid
//   - a virtual SFTP root: clients cannot climb out of it with ".."
//   - strictly read-only: every write request is refused and logged
//   - LSTAT does not follow symlinks, exactly like OpenSSH's sftp-server
//
// It binds 127.0.0.1 only, so nothing on the network can reach it.
package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"path"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/pkg/sftp"
	"golang.org/x/crypto/ssh"
)

const (
	defaultPort = 2222
	defaultUser = "test"
	defaultPass = "scibridge"
)

type listerAt []os.FileInfo

func (l listerAt) ListAt(f []os.FileInfo, offset int64) (int, error) {
	if offset >= int64(len(l)) {
		return 0, io.EOF
	}
	n := copy(f, l[offset:])
	if n < len(f) {
		return n, io.EOF
	}
	return n, nil
}

// rootFS serves one directory tree, read-only, mapped from absolute SFTP paths.
type rootFS struct {
	root   string
	logMu  sync.Mutex
	logTo  io.Writer
	opLog  *os.File
	quiet  bool
}

// virtualToReal maps an SFTP path onto the real filesystem inside root.
// path.Clean collapses any ".." that would climb above the root, so the mapping
// cannot escape lexically. Symlinks inside the tree are left alone on purpose:
// that is what lets a client's symlink policy be tested.
func (fs *rootFS) virtualToReal(virtual string) (string, error) {
	if strings.ContainsRune(virtual, 0) {
		return "", os.ErrInvalid
	}
	clean := path.Clean("/" + strings.ReplaceAll(virtual, `\`, "/"))
	rel := strings.TrimPrefix(clean, "/")
	real := filepath.Join(fs.root, filepath.FromSlash(rel))

	if real != fs.root && !strings.HasPrefix(real, fs.root+string(os.PathSeparator)) {
		return "", os.ErrPermission
	}
	return real, nil
}

func (fs *rootFS) record(op, name string, extra map[string]any) {
	if fs.opLog != nil {
		entry := map[string]any{"op": op, "path": name, "t": time.Now().Format(time.RFC3339Nano)}
		for k, v := range extra {
			entry[k] = v
		}
		if line, err := json.Marshal(entry); err == nil {
			fs.logMu.Lock()
			fmt.Fprintln(fs.opLog, string(line))
			fs.logMu.Unlock()
		}
	}

	switch {
	case strings.HasPrefix(op, "WRITE"):
		fmt.Fprintf(os.Stderr, "  [拒绝写入] %s %s —— 本测试服务器是只读的\n", op, name)
	case !fs.quiet:
		fmt.Printf("  %-10s %s\n", op, name)
	}
}

func (fs *rootFS) statOf(real string, follow bool) (os.FileInfo, error) {
	if follow {
		return os.Stat(real)
	}
	return os.Lstat(real)
}

// Filelist handles List (readdir) and Stat (which follows symlinks).
func (fs *rootFS) Filelist(r *sftp.Request) (sftp.ListerAt, error) {
	real, err := fs.virtualToReal(r.Filepath)
	if err != nil {
		return nil, err
	}

	switch r.Method {
	case "List":
		fs.record("list", r.Filepath, nil)
		entries, err := os.ReadDir(real)
		if err != nil {
			return nil, err
		}
		out := make(listerAt, 0, len(entries))
		for _, entry := range entries {
			info, err := entry.Info()
			if err != nil {
				continue // a vanished entry must not fail the whole listing
			}
			out = append(out, info)
		}
		return out, nil

	case "Stat":
		fs.record("stat", r.Filepath, nil)
		info, err := fs.statOf(real, true)
		if err != nil {
			return nil, err
		}
		return listerAt{info}, nil
	}

	return nil, os.ErrInvalid
}

// Lstat handles SSH_FXP_LSTAT. Implementing this method is what makes the
// server report symlinks as symlinks instead of silently resolving them; without
// it pkg/sftp would fall back to Stat and follow the link.
func (fs *rootFS) Lstat(r *sftp.Request) (sftp.ListerAt, error) {
	real, err := fs.virtualToReal(r.Filepath)
	if err != nil {
		return nil, err
	}
	fs.record("lstat", r.Filepath, nil)
	info, err := fs.statOf(real, false)
	if err != nil {
		return nil, err
	}
	return listerAt{info}, nil
}

func (fs *rootFS) Fileread(r *sftp.Request) (io.ReaderAt, error) {
	if r.Pflags().Write {
		fs.record("WRITE_open", r.Filepath, map[string]any{"flags": r.Flags})
		return nil, os.ErrPermission
	}
	real, err := fs.virtualToReal(r.Filepath)
	if err != nil {
		return nil, err
	}
	fs.record("read", r.Filepath, nil)
	// *os.File satisfies io.ReaderAt and io.Closer, and the request server closes it.
	return os.Open(real)
}

func (fs *rootFS) Filewrite(r *sftp.Request) (io.WriterAt, error) {
	fs.record("WRITE_"+strings.ToLower(r.Method), r.Filepath, nil)
	return nil, os.ErrPermission
}

func (fs *rootFS) Filecmd(r *sftp.Request) error {
	fs.record("WRITE_"+strings.ToLower(r.Method), r.Filepath, nil)
	return os.ErrPermission
}

// ------------------------------------------------------------------ host key

func loadOrCreateHostKey(path string) (ssh.Signer, bool, error) {
	if data, err := os.ReadFile(path); err == nil {
		signer, err := ssh.ParsePrivateKey(data)
		if err == nil {
			return signer, false, nil
		}
		fmt.Fprintf(os.Stderr, "警告：主机密钥文件无法解析，将重新生成：%v\n", err)
	}

	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, false, err
	}
	block, err := ssh.MarshalPrivateKey(priv, "scibridge-testserver")
	if err != nil {
		return nil, false, err
	}
	if err := os.WriteFile(path, pem.EncodeToMemory(block), 0o600); err != nil {
		return nil, false, err
	}
	signer, err := ssh.NewSignerFromKey(priv)
	return signer, true, err
}

func fingerprint(signer ssh.Signer) string {
	sum := sha256.Sum256(signer.PublicKey().Marshal())
	return "SHA256:" + base64.RawStdEncoding.EncodeToString(sum[:])
}

// ------------------------------------------------------------------ SSH glue

func handleConn(conn net.Conn, cfg *ssh.ServerConfig, fs *rootFS) {
	defer conn.Close()

	sshConn, chans, reqs, err := ssh.NewServerConn(conn, cfg)
	if err != nil {
		// Wrong password and protocol errors both land here; keep it quiet.
		return
	}
	defer sshConn.Close()
	go ssh.DiscardRequests(reqs)

	for newChannel := range chans {
		if newChannel.ChannelType() != "session" {
			_ = newChannel.Reject(ssh.UnknownChannelType, "only session channels are supported")
			continue
		}
		channel, requests, err := newChannel.Accept()
		if err != nil {
			continue
		}
		go handleSession(channel, requests, fs)
	}
}

func handleSession(channel ssh.Channel, requests <-chan *ssh.Request, fs *rootFS) {
	defer channel.Close()

	for req := range requests {
		switch req.Type {
		case "subsystem":
			if len(req.Payload) < 4 {
				_ = req.Reply(false, nil)
				continue
			}
			name := string(req.Payload[4:])
			if name != "sftp" {
				_ = req.Reply(false, nil)
				continue
			}
			_ = req.Reply(true, nil)
			server := sftp.NewRequestServer(channel, sftp.Handlers{
				FileGet:  fs,
				FilePut:  fs,
				FileCmd:  fs,
				FileList: fs,
			})
			if err := server.Serve(); err != nil && !errors.Is(err, io.EOF) {
				fmt.Fprintf(os.Stderr, "  SFTP 会话结束：%v\n", err)
			}
			_ = server.Close()
			return
		default:
			// No shell, no exec, no pty: this server only carries SFTP.
			_ = req.Reply(false, nil)
		}
	}
}

// --------------------------------------------------------------------- seed

func seed(base, root string) {
	mustWrite := func(rel string, data []byte) {
		full := filepath.Join(root, filepath.FromSlash(rel))
		if _, err := os.Stat(full); err == nil {
			return // never overwrite the user's own test edits
		}
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			return
		}
		_ = os.WriteFile(full, data, 0o644)
	}

	mustWrite("README.txt", []byte(
		"SciBridge 测试数据\r\n"+
			"这是本机测试服务器提供的示例数据，用来验证 SciBridge 的只读读取流程。\r\n"+
			"你可以随意增删这里的文件。\r\n"))

	mustWrite("data/observations.csv", []byte(
		"date,station,temperature_c,wind_speed_ms\r\n"+
			"2024-01-01,A01,-3.5,2.1\r\n"+
			"2024-01-02,A01,-4.1,1.8\r\n"+
			"2024-01-03,A02,-2.8,3.4\r\n"))

	// 9 MiB: large enough to force multi-chunk reads and the "not finished" notice.
	big := filepath.Join(root, "data", "big.bin")
	if _, err := os.Stat(big); err != nil {
		if err := os.MkdirAll(filepath.Dir(big), 0o755); err == nil {
			if f, err := os.Create(big); err == nil {
				const size = 9 * 1024 * 1024
				buf := make([]byte, 64*1024)
				written := 0
				for written < size {
					for i := range buf {
						buf[i] = byte((written + i) * 37 % 251)
					}
					n, err := f.Write(buf)
					if err != nil {
						break
					}
					written += n
				}
				_ = f.Close()
			}
		}
	}

	mustWrite("data/deep/a/b/c/leaf.txt", []byte("deep leaf file\n"))
	mustWrite("数据/观测记录.txt", []byte("站点：A01\r\n温度：21.5\r\n"))

	for i := 0; i < 50; i++ {
		mustWrite(fmt.Sprintf("many/f%03d.txt", i), []byte(fmt.Sprintf("file %d\n", i)))
	}

	// A symlink pointing outside the served root, to exercise the client's policy.
	outside := filepath.Join(base, "outside", "secret.txt")
	if _, err := os.Stat(outside); err != nil {
		if err := os.MkdirAll(filepath.Dir(outside), 0o755); err == nil {
			_ = os.WriteFile(outside, []byte("这个文件在可读根目录之外，正常情况下读不到。\n"), 0o644)
		}
	}
	link := filepath.Join(root, "link_out")
	if _, err := os.Lstat(link); err != nil {
		if err := os.Symlink(filepath.Join(base, "outside"), link); err != nil {
			fmt.Println("  注意：创建符号链接失败（Windows 默认需要开发者模式或管理员权限）。")
			fmt.Println("        这不是错误，只是少了一个“符号链接应被拒绝”的测试项。")
		}
	}
}

// --------------------------------------------------------------------- main

func defaultBase() string {
	if home, err := os.UserHomeDir(); err == nil && home != "" {
		return filepath.Join(home, "SciBridgeTestData")
	}
	return filepath.Join(os.TempDir(), "SciBridgeTestData")
}

func main() {
	port := flag.Int("port", defaultPort, "监听端口（只绑定 127.0.0.1）")
	root := flag.String("root", "", "对外提供的目录（默认 <用户目录>/SciBridgeTestData/sftp-root）")
	user := flag.String("user", defaultUser, "测试账号")
	pass := flag.String("pass", defaultPass, "测试密码")
	base := flag.String("base", "", "测试数据根目录（默认 <用户目录>/SciBridgeTestData）")
	logPath := flag.String("log", "", "把每个 SFTP 请求追加记录到该文件（JSON 行）")
	fingerprintPath := flag.String("fingerprint-file", "", "把主机指纹写入该文件（便于自动化核对）")
	quiet := flag.Bool("quiet", false, "不打印每个请求，只打印关键信息")
	noSeed := flag.Bool("no-seed", false, "不生成示例数据")
	flag.Parse()

	enableUTF8Console()

	baseDir := *base
	if baseDir == "" {
		baseDir = defaultBase()
	}
	rootDir := *root
	if rootDir == "" {
		rootDir = filepath.Join(baseDir, "sftp-root")
	}
	absRoot, err := filepath.Abs(rootDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "无法解析目录 %q：%v\n", rootDir, err)
		os.Exit(1)
	}
	if err := os.MkdirAll(absRoot, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "无法创建目录 %q：%v\n", absRoot, err)
		os.Exit(1)
	}

	if !*noSeed {
		seed(filepath.Dir(absRoot), absRoot)
	}

	exePath, err := os.Executable()
	if err != nil {
		exePath = "."
	}
	keyPath := filepath.Join(filepath.Dir(exePath), "testserver_hostkey")
	signer, created, err := loadOrCreateHostKey(keyPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "无法准备主机密钥：%v\n", err)
		os.Exit(1)
	}

	serverFingerprint := fingerprint(signer)
	if *fingerprintPath != "" {
		if err := os.WriteFile(*fingerprintPath, []byte(serverFingerprint+"\n"), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "无法写入指纹文件：%v\n", err)
		}
	}

	fs := &rootFS{root: absRoot, quiet: *quiet, logTo: os.Stderr}
	if *logPath != "" {
		f, err := os.OpenFile(*logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
		if err != nil {
			fmt.Fprintf(os.Stderr, "无法写入日志文件 %q：%v\n", *logPath, err)
			os.Exit(1)
		}
		defer f.Close()
		fs.opLog = f
	}

	cfg := &ssh.ServerConfig{
		PasswordCallback: func(c ssh.ConnMetadata, password []byte) (*ssh.Permissions, error) {
			ok := c.User() == *user &&
				subtle.ConstantTimeCompare(password, []byte(*pass)) == 1
			// Recorded so a test can prove how many attempts a client made:
			// institutions commonly lock an account after a few failures.
			fs.record("auth", c.User(), map[string]any{"ok": ok})
			if !ok {
				time.Sleep(400 * time.Millisecond) // slow down guessing
				return nil, fmt.Errorf("密码不正确")
			}
			return nil, nil
		},
	}
	cfg.AddHostKey(signer)

	address := fmt.Sprintf("127.0.0.1:%d", *port)
	listener, err := net.Listen("tcp", address)
	if err != nil {
		fmt.Fprintf(os.Stderr, "\n无法监听 %s：%v\n\n", address, err)
		fmt.Fprintln(os.Stderr, "端口可能已被占用。换一个端口再试，例如：")
		fmt.Fprintf(os.Stderr, "  %s -port %d\n", filepath.Base(exePath), *port+1)
		os.Exit(1)
	}

	fmt.Println()
	fmt.Println("=========================================================")
	fmt.Println("  SciBridge 本机测试服务器（只读）")
	fmt.Println("=========================================================")
	fmt.Printf("  监听地址   : %s（仅本机可访问）\n", address)
	fmt.Printf("  提供目录   : %s\n", absRoot)
	if *logPath != "" {
		fmt.Printf("  请求日志   : %s\n", *logPath)
	}
	fmt.Printf("  主机指纹   : %s\n", serverFingerprint)
	if created {
		fmt.Println("              （首次运行已生成主机密钥，保存在 exe 同目录）")
	}
	fmt.Println()
	fmt.Println("  在 SciBridge 窗口里这样填：")
	fmt.Printf("    服务器地址   : 127.0.0.1\n")
	fmt.Printf("    端口         : %d\n", *port)
	fmt.Printf("    账号         : %s\n", *user)
	fmt.Printf("    密码         : %s\n", *pass)
	fmt.Printf("    可读根目录   : .\n")
	fmt.Println()
	fmt.Println("  按 Ctrl+C 停止。")
	fmt.Println("---------------------------------------------------------")

	for {
		conn, err := listener.Accept()
		if err != nil {
			fmt.Fprintf(os.Stderr, "接受连接失败：%v\n", err)
			continue
		}
		go handleConn(conn, cfg, fs)
	}
}

