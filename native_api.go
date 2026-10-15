package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"sync"
)

type nativeRequest struct {
	Action    string `json:"action"`
	Path      string `json:"path"`
	Content   string `json:"content,omitempty"`
	Overwrite bool   `json:"overwrite,omitempty"`
	ForceSync bool   `json:"forceSync,omitempty"`
	MimeType  string `json:"mimeType,omitempty"`
}

var nativeCallMu sync.Mutex

func invokeNativeRequest(requestJSON string) FileResponse {
	var request nativeRequest
	if err := json.Unmarshal([]byte(requestJSON), &request); err != nil {
		return FileResponse{Success: false, Error: "Geçersiz JSON isteği: " + err.Error()}
	}
	if request.Path == "" {
		return FileResponse{Success: false, Error: "path alanı gereklidir"}
	}

	var content []byte
	if request.Action == "write" {
		decoded, err := base64.StdEncoding.DecodeString(request.Content)
		if err != nil {
			return FileResponse{Success: false, Error: "content geçerli base64 değil: " + err.Error()}
		}
		content = decoded
	} else if request.Action != "read" {
		return FileResponse{Success: false, Error: "action yalnızca 'read' veya 'write' olabilir"}
	}

	nativeCallMu.Lock()
	defer nativeCallMu.Unlock()

	if request.Action == "read" {
		return readFileFromSMB(request.Path)
	}

	return writeFileToSMB(
		request.Path,
		bytes.NewReader(content),
		request.Overwrite,
		request.MimeType,
		request.ForceSync,
	)
}

func marshalNativeResponse(response FileResponse) string {
	result, err := json.Marshal(response)
	if err != nil {
		return fmt.Sprintf(`{"success":false,"error":%q}`, err.Error())
	}
	return string(result)
}
