# whatsapp-bridge (Go) image.
#
# The bridge is CGO-free (modernc.org/sqlite), so it builds to a static binary and runs on
# a distroless base. ca-certificates (in distroless) are needed for the TLS connection to
# WhatsApp. Runs as root so it can write the session DB + media into the mounted volumes.

# ---- build ------------------------------------------------------------------
FROM golang:1.26 AS build
WORKDIR /src

COPY go.mod go.sum ./
RUN go mod download

COPY . .
# CGO disabled => fully static binary; safe on a minimal runtime image.
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/whatsapp-bridge .

# ---- runtime ----------------------------------------------------------------
FROM gcr.io/distroless/static-debian12 AS runtime
COPY --from=build /out/whatsapp-bridge /usr/local/bin/whatsapp-bridge

# Documentation only — not published to the host; reachable on the private compose network.
EXPOSE 8088

ENTRYPOINT ["/usr/local/bin/whatsapp-bridge"]
