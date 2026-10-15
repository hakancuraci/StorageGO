//go:build cgo

package main

/*
#include <stdlib.h>
#include <stdint.h>
*/
import "C"

import "unsafe"

//export StorageGO_CreateClient
func StorageGO_CreateClient(configJSON *C.char) *C.char {
	if configJSON == nil {
		return C.CString(marshalCreateResponse(nativeCreateResponse{Success: false, Error: "configJSON NULL olamaz"}))
	}
	return C.CString(createNativeClientSafely(C.GoString(configJSON)))
}

//export StorageGO_Invoke
func StorageGO_Invoke(clientID C.uint64_t, requestJSON *C.char) *C.char {
	if requestJSON == nil {
		return C.CString(marshalNativeResponse(failedFileResponse("requestJSON NULL olamaz")))
	}
	return C.CString(invokeNativeRequestSafely(uint64(clientID), C.GoString(requestJSON)))
}

//export StorageGO_CloseClient
func StorageGO_CloseClient(clientID C.uint64_t) *C.char {
	return C.CString(closeNativeClientSafely(uint64(clientID)))
}

//export StorageGO_FreeResult
func StorageGO_FreeResult(result *C.char) {
	if result != nil {
		C.free(unsafe.Pointer(result))
	}
}
