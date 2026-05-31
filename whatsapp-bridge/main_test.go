package main

import "testing"

// ownerUser is the gate that decides whose messages get relayed, so its JID
// normalisation (stripping @server and :device suffixes) is worth pinning down.
func TestOwnerUser(t *testing.T) {
	tests := []struct {
		name     string
		ownerJID string
		want     string
	}{
		{"empty", "", ""},
		{"bare number", "4915112345678", "4915112345678"},
		{"phone JID", "4915112345678@s.whatsapp.net", "4915112345678"},
		{"phone JID with device suffix", "4915112345678:3@s.whatsapp.net", "4915112345678"},
		{"lid JID", "123456789@lid", "123456789"},
		{"device suffix without server", "4915112345678:12", "4915112345678"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c := Config{OwnerJID: tt.ownerJID}
			if got := c.ownerUser(); got != tt.want {
				t.Errorf("ownerUser() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestGetenvDefault(t *testing.T) {
	t.Run("returns default when unset/empty", func(t *testing.T) {
		t.Setenv("ERDA_BRIDGE_TEST_KEY", "")
		if got := getenvDefault("ERDA_BRIDGE_TEST_KEY", "fallback"); got != "fallback" {
			t.Errorf("getenvDefault = %q, want fallback", got)
		}
	})
	t.Run("returns value when set", func(t *testing.T) {
		t.Setenv("ERDA_BRIDGE_TEST_KEY", "actual")
		if got := getenvDefault("ERDA_BRIDGE_TEST_KEY", "fallback"); got != "actual" {
			t.Errorf("getenvDefault = %q, want actual", got)
		}
	})
}
