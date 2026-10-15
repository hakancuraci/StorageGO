package main

import (
	"errors"
	"os"
)

// Unknown/transport errors remain conservative: never replay uncertain writes.
func reusableNativeFileError(err error) bool {
	return errors.Is(err, os.ErrNotExist) || errors.Is(err, os.ErrExist) || errors.Is(err, os.ErrPermission)
}
