package main

import "testing"

// secretEqual guards POST /send, so verify it accepts only an exact match.
func TestSecretEqual(t *testing.T) {
	tests := []struct {
		name      string
		got, want string
		equal     bool
	}{
		{"exact match", "s3cret", "s3cret", true},
		{"mismatch", "wrong", "s3cret", false},
		{"different length", "s3cre", "s3cret", false},
		{"empty got", "", "s3cret", false},
		{"both empty", "", "", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := secretEqual(tt.got, tt.want); got != tt.equal {
				t.Errorf("secretEqual(%q, %q) = %v, want %v", tt.got, tt.want, got, tt.equal)
			}
		})
	}
}
