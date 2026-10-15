package main

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"sync"
	"sync/atomic"
)

type nativeRequest struct {
	Action    string `json:"action"`
	Path      string `json:"path"`
	Content   string `json:"content,omitempty"`
	Overwrite bool   `json:"overwrite,omitempty"`
	ForceSync bool   `json:"forceSync,omitempty"`
	MimeType  string `json:"mimeType,omitempty"`
}

type nativeCreateResponse struct {
	Success  bool   `json:"success"`
	ClientID uint64 `json:"clientId,omitempty"`
	Error    string `json:"error,omitempty"`
}

var nativeClients = struct {
	sync.RWMutex
	items map[uint64]*nativeSMBClient
}{items: make(map[uint64]*nativeSMBClient)}

var nextNativeClientID atomic.Uint64

func createNativeClient(configJSON string) nativeCreateResponse {
	var config NativeClientConfig
	if err := json.Unmarshal([]byte(configJSON), &config); err != nil {
		return nativeCreateResponse{Success: false, Error: "Geçersiz client JSON: " + err.Error()}
	}
	client, err := newNativeSMBClient(config)
	if err != nil {
		return nativeCreateResponse{Success: false, Error: err.Error()}
	}
	id := nextNativeClientID.Add(1)
	nativeClients.Lock()
	nativeClients.items[id] = client
	nativeClients.Unlock()
	return nativeCreateResponse{Success: true, ClientID: id}
}

func closeNativeClient(clientID uint64) nativeCreateResponse {
	nativeClients.Lock()
	client, found := nativeClients.items[clientID]
	if found {
		delete(nativeClients.items, clientID)
	}
	nativeClients.Unlock()
	if !found {
		return nativeCreateResponse{Success: false, Error: "client bulunamadı veya zaten kapatıldı"}
	}
	client.close()
	return nativeCreateResponse{Success: true}
}

func getNativeClient(clientID uint64) (*nativeSMBClient, bool) {
	nativeClients.RLock()
	client, found := nativeClients.items[clientID]
	nativeClients.RUnlock()
	return client, found
}

func invokeNativeRequest(clientID uint64, requestJSON string) FileResponse {
	var request nativeRequest
	if err := json.Unmarshal([]byte(requestJSON), &request); err != nil {
		return failedFileResponse("Geçersiz JSON isteği: " + err.Error())
	}
	if request.Path == "" {
		return failedFileResponse("path alanı gereklidir")
	}

	var content []byte
	if request.Action == "write" {
		decoded, err := base64.StdEncoding.DecodeString(request.Content)
		if err != nil {
			return failedFileResponse("content geçerli base64 değil: " + err.Error())
		}
		content = decoded
	} else if request.Action != "read" {
		return failedFileResponse("action yalnızca 'read' veya 'write' olabilir")
	}

	client, found := getNativeClient(clientID)
	if !found {
		return failedFileResponse("client bulunamadı veya kapatıldı")
	}
	if request.Action == "read" {
		return client.read(request.Path)
	}
	return client.write(request.Path, content, request.Overwrite, request.ForceSync, request.MimeType)
}

func marshalNativeResponse(response FileResponse) string {
	result, err := json.Marshal(response)
	if err != nil {
		return fmt.Sprintf(`{"success":false,"error":%q}`, err.Error())
	}
	return string(result)
}

func marshalCreateResponse(response nativeCreateResponse) string {
	result, err := json.Marshal(response)
	if err != nil {
		return fmt.Sprintf(`{"success":false,"error":%q}`, err.Error())
	}
	return string(result)
}

// The C ABI must not let a Go panic cross the native boundary because that can
// terminate the hosting .NET process.
func createNativeClientSafely(configJSON string) (result string) {
	defer func() {
		if recovered := recover(); recovered != nil {
			result = marshalCreateResponse(nativeCreateResponse{
				Success: false,
				Error:   fmt.Sprintf("beklenmeyen native hata: %v", recovered),
			})
		}
	}()
	return marshalCreateResponse(createNativeClient(configJSON))
}

func invokeNativeRequestSafely(clientID uint64, requestJSON string) (result string) {
	defer func() {
		if recovered := recover(); recovered != nil {
			result = marshalNativeResponse(failedFileResponse(fmt.Sprintf("beklenmeyen native hata: %v", recovered)))
		}
	}()
	return marshalNativeResponse(invokeNativeRequest(clientID, requestJSON))
}

func closeNativeClientSafely(clientID uint64) (result string) {
	defer func() {
		if recovered := recover(); recovered != nil {
			result = marshalCreateResponse(nativeCreateResponse{
				Success: false,
				Error:   fmt.Sprintf("beklenmeyen native hata: %v", recovered),
			})
		}
	}()
	return marshalCreateResponse(closeNativeClient(clientID))
}
