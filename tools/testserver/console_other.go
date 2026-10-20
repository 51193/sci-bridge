//go:build !windows

package main

// enableUTF8Console is a no-op outside Windows; terminals there are already UTF-8.
func enableUTF8Console() {}
