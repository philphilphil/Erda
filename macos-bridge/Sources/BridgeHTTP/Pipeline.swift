import NIOCore
import NIOHTTP1

/// The channel pipeline from dossier §2.2 — **with two corrections taken from the NIO source**
/// (`NIOHTTP1/HTTPPipelineSetup.swift`, `_configureHTTPServerPipeline`), which the dossier said
/// it had only read partially:
///
/// 1. `HTTPResponseEncoder` comes **before** the decoder, not after it. Outbound writes travel
///    tail-to-head, so the encoder has to sit nearest the head to be the last handler a response
///    passes through.
/// 2. `NIOHTTPResponseHeadersValidator` sits between the pipeline handler and the error handler.
///    The dossier omits it; it is what stops a response-splitting attack via a header value, so
///    it stays.
///
/// `configureHTTPServerPipeline` cannot be used despite matching this shape, because it takes no
/// `limitConfiguration` in its public form — the 16 KiB header caps are the entire point.
enum Pipeline {
    static func handlers(configuration: BridgeServerConfiguration) -> [ChannelHandler] {
        var limits = NIOHTTPDecoderLimitConfiguration()
        limits.maxHeaderFieldSize = configuration.maxHeaderFieldSize
        limits.maxHeaderListSize = configuration.maxHeaderListSize
        limits.maxHeaderFieldCount = configuration.maxHeaderFieldCount

        return [
            // Slowloris: a peer that opens a connection and dribbles bytes is closed by the
            // read timeout long before it costs anything.
            IdleStateHandler(
                readTimeout: .seconds(configuration.readTimeoutSeconds),
                writeTimeout: .seconds(configuration.writeTimeoutSeconds),
                allTimeout: .seconds(configuration.allTimeoutSeconds)
            ),
            IdleCloseHandler(),
            HTTPResponseEncoder(),
            // Closest to the head of the *outbound* path after the encoder, so it sees every
            // response — ours, the aggregator's 413 and the protocol handler's 400 alike.
            CloseAfterResponseHandler(),
            ByteToMessageHandler(
                HTTPRequestDecoder(
                    leftOverBytesStrategy: .dropBytes,
                    informationalResponseStrategy: .drop,
                    limitConfiguration: limits
                )
            ),
            HTTPServerPipelineHandler(),
            NIOHTTPResponseHeadersValidator(),
            HTTPServerProtocolErrorHandler(),
            // Emits the 413 itself when `Content-Length` or the accumulated body exceeds the cap.
            NIOHTTPServerRequestAggregator(maxContentLength: configuration.maxBodyBytes),
        ]
    }
}

/// Closes the connection when the idle timeouts fire.
///
/// `IdleStateHandler` only *reports* idleness as a user inbound event; without something to act
/// on it, a half-open or dribbling connection would sit in the accept budget indefinitely.
final class IdleCloseHandler: ChannelInboundHandler {
    typealias InboundIn = NIOAny
    typealias InboundOut = NIOAny

    func userInboundEventTriggered(context: ChannelHandlerContext, event: Any) {
        if event is IdleStateHandler.IdleStateEvent {
            context.close(promise: nil)
        } else {
            context.fireUserInboundEventTriggered(event)
        }
    }
}

/// Closes the connection once a complete response has been flushed.
///
/// The bridge deliberately does **not** keep connections alive. Three reasons, in order of
/// weight:
///
/// 1. `HTTPRequestDecoder`'s initializer that links the response encoder — the one that stops a
///    `HEAD` response from carrying a body — is `internal` in NIO, so we cannot use it. On a
///    reused connection a stray body would mis-frame the *next* response; on a closed one it
///    cannot.
/// 2. It makes every rejection path uniform: the aggregator's 413 and the protocol error
///    handler's 400 are written by NIO itself and neither closes the channel, so without this
///    they would linger until the read timeout.
/// 3. There is one client issuing at most 30 requests a minute over a LAN. A TCP handshake per
///    request is not a cost worth reasoning about.
final class CloseAfterResponseHandler: ChannelOutboundHandler {
    typealias OutboundIn = HTTPServerResponsePart
    typealias OutboundOut = HTTPServerResponsePart

    func write(context: ChannelHandlerContext, data: NIOAny, promise: EventLoopPromise<Void>?) {
        guard case .end = Self.unwrapOutboundIn(data) else {
            context.write(data, promise: promise)
            return
        }

        // Capture the `Channel`, never the non-`Sendable` `ChannelHandlerContext`, and close
        // only once the write has actually completed — closing on the write call itself would
        // truncate the response.
        let channel = context.channel
        let written = context.eventLoop.makePromise(of: Void.self)
        if let promise { written.futureResult.cascade(to: promise) }
        context.write(data, promise: written)
        context.flush()
        written.futureResult.whenComplete { _ in channel.close(promise: nil) }
    }
}
