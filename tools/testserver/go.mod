module scibridge/testserver

go 1.24

require (
	github.com/pkg/sftp v1.13.9
	golang.org/x/crypto v0.31.0
)

require (
	github.com/kr/fs v0.1.0 // indirect
	golang.org/x/sys v0.28.0 // indirect
)

// The dependency sources are vendored under third_party/ so this builds with no
// network access and no module proxy. They are the unmodified upstream releases:
//   github.com/pkg/sftp v1.13.9
//   golang.org/x/crypto v0.31.0
//   golang.org/x/sys v0.30.0
//   github.com/kr/fs v0.1.0
replace github.com/pkg/sftp => ../../third_party/sftp

replace golang.org/x/crypto => ../../third_party/crypto

replace golang.org/x/sys => ../../third_party/sys

replace github.com/kr/fs => ../../third_party/fs
