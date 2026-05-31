package main

import (
	"testing"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/proto"
)

func TestIsTextMessage(t *testing.T) {
	tests := []struct {
		name string
		msg  *waE2E.Message
		want bool
	}{
		{"nil", nil, false},
		{"empty", &waE2E.Message{}, false},
		{"conversation", &waE2E.Message{Conversation: proto.String("hi")}, true},
		{
			"extended text",
			&waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("hi")}},
			true,
		},
		{
			"image (not text)",
			&waE2E.Message{ImageMessage: &waE2E.ImageMessage{Caption: proto.String("cap")}},
			false,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := isTextMessage(tt.msg); got != tt.want {
				t.Errorf("isTextMessage() = %v, want %v", got, tt.want)
			}
		})
	}
}

func TestTextBody(t *testing.T) {
	tests := []struct {
		name string
		msg  *waE2E.Message
		want string
	}{
		{"empty", &waE2E.Message{}, ""},
		{"conversation", &waE2E.Message{Conversation: proto.String("hello")}, "hello"},
		{
			"extended text",
			&waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("world")}},
			"world",
		},
		{
			"conversation wins over extended text",
			&waE2E.Message{
				Conversation:        proto.String("primary"),
				ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("secondary")},
			},
			"primary",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := textBody(tt.msg); got != tt.want {
				t.Errorf("textBody() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestExtensionForMime(t *testing.T) {
	tests := []struct {
		mime string
		want string
	}{
		{"audio/ogg", ".ogg"},
		{"audio/ogg; codecs=opus", ".ogg"}, // parameters are stripped
		{"AUDIO/OGG", ".ogg"},              // case-insensitive
		{"audio/mpeg", ".mp3"},
		{"audio/mp4", ".m4a"},
		{"image/jpeg", ".jpg"},
		{"image/png", ".png"},
		{"image/webp", ".webp"},
		{"audio/unknown-codec", ".ogg"}, // audio/* family fallback
		{"image/unknown", ".jpg"},       // image/* family fallback
		{"application/pdf", ".bin"},     // last-resort fallback
		{"", ".bin"},
	}
	for _, tt := range tests {
		t.Run(tt.mime, func(t *testing.T) {
			if got := extensionForMime(tt.mime); got != tt.want {
				t.Errorf("extensionForMime(%q) = %q, want %q", tt.mime, got, tt.want)
			}
		})
	}
}
