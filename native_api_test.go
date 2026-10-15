package main

import (
	"encoding/json"
	"testing"
)

func TestInvokeNativeRequestValidatesRequestWithoutSMBAccess(t *testing.T) {
	tests := []struct {
		name      string
		request   string
		wantError string
	}{
		{
			name:      "invalid JSON",
			request:   "{",
			wantError: "Geçersiz JSON isteği:",
		},
		{
			name:      "missing path",
			request:   `{"action":"read"}`,
			wantError: "path alanı gereklidir",
		},
		{
			name:      "unsupported action",
			request:   `{"action":"remove","path":"file.txt"}`,
			wantError: "action yalnızca 'read' veya 'write' olabilir",
		},
		{
			name:      "invalid write content",
			request:   `{"action":"write","path":"file.txt","content":"!"}`,
			wantError: "content geçerli base64 değil:",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			response := invokeNativeRequest(test.request)
			if response.Success {
				t.Fatal("expected request to fail validation")
			}
			if len(response.Error) < len(test.wantError) || response.Error[:len(test.wantError)] != test.wantError {
				t.Fatalf("error = %q, want prefix %q", response.Error, test.wantError)
			}
		})
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
