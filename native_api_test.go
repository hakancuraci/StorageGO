package main

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestInvokeNativeRequestValidatesRequestWithoutSMBAccess(t *testing.T) {
	created := createNativeClient(`{"server":"localhost","shareName":"share","username":"user","password":"secret"}`)
	if !created.Success {
		t.Fatalf("createNativeClient failed: %s", created.Error)
	}
	defer closeNativeClient(created.ClientID)

	tests := []struct {
		name      string
		request   string
		wantError string
	}{
		{"invalid JSON", "{", "Geçersiz JSON isteği:"},
		{"missing path", `{"action":"read"}`, "path alanı gereklidir"},
		{"unsupported action", `{"action":"remove","path":"file.txt"}`, "action yalnızca 'read' veya 'write' olabilir"},
		{"invalid write content", `{"action":"write","path":"file.txt","content":"!"}`, "content geçerli base64 değil:"},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			response := invokeNativeRequest(created.ClientID, test.request)
			if response.Success || !strings.HasPrefix(response.Error, test.wantError) {
				t.Fatalf("response = %#v, want error prefix %q", response, test.wantError)
			}
		})
	}
}

func TestNativeClientLifecycle(t *testing.T) {
	invalid := createNativeClient(`{"server":"","shareName":"share","username":"user"}`)
	if invalid.Success || invalid.Error == "" {
		t.Fatalf("invalid config response = %#v", invalid)
	}

	created := createNativeClient(`{"server":"localhost","shareName":"share","username":"user"}`)
	if !created.Success || created.ClientID == 0 {
		t.Fatalf("create response = %#v", created)
	}
	if closed := closeNativeClient(created.ClientID); !closed.Success {
		t.Fatalf("close response = %#v", closed)
	}
	response := invokeNativeRequest(created.ClientID, `{"action":"read","path":"file.txt"}`)
	if response.Success || response.Error != "client bulunamadı veya kapatıldı" {
		t.Fatalf("invoke after close response = %#v", response)
	}
}

func TestNativeClientConfigDefaults(t *testing.T) {
	config := NativeClientConfig{Server: "server", ShareName: `\\share\\`, Username: "user"}
	client, err := newNativeSMBClient(config)
	if err != nil {
		t.Fatalf("newNativeSMBClient returned error: %v", err)
	}
	defer client.close()

	if client.config.Port != defaultSMBPort || client.config.DialTimeoutSeconds != 15 {
		t.Fatalf("defaults were not applied: %#v", client.config)
	}
	if client.config.ShareName != "share" || client.config.MaxReadBytes != defaultMaxReadBytes {
		t.Fatalf("normalized config = %#v", client.config)
	}
}

func TestMarshalNativeResponseProducesJSON(t *testing.T) {
	response := FileResponse{Success: false, Error: "failed"}
	encoded := marshalNativeResponse(response)
	var decoded FileResponse
	if err := json.Unmarshal([]byte(encoded), &decoded); err != nil {
		t.Fatalf("response is not valid JSON: %v", err)
	}
	if decoded.Success || decoded.Error != response.Error {
		t.Fatalf("decoded response = %#v, want %#v", decoded, response)
	}
}
