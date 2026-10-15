package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"unsafe"
)

//export StorageGO_Invoke
func StorageGO_Invoke(requestJSON *C.char) *C.char {
	if requestJSON == nil {
		return C.CString(marshalNativeResponse(FileResponse{
			Success: false,
			Error:   "requestJSON NULL olamaz",
		}))
	}

	response := invokeNativeRequest(C.GoString(requestJSON))
	return C.CString(marshalNativeResponse(response))
}

//export StorageGO_FreeResult
func StorageGO_FreeResult(result *C.char) {
	if result != nil {
		C.free(unsafe.Pointer(result))
	}
}
