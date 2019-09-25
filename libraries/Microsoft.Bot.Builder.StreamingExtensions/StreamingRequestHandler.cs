﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Schema;
using Microsoft.Bot.StreamingExtensions;
using Microsoft.Bot.StreamingExtensions.Transport;
using Microsoft.Bot.StreamingExtensions.Transport.NamedPipes;
using Microsoft.Bot.StreamingExtensions.Transport.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.StreamingExtensions
{
    internal class StreamingRequestHandler : RequestHandler
    {
        private readonly ILogger _logger;
        private readonly DirectLineAdapter _adapter;
        private readonly string _userAgent;
        private readonly SortedSet<string> _conversations;

        // TODO: this is a placeholder until the well defined reconnection path is determined.
        private string _reconnectPath = "api/reconnect";
        private IStreamingTransportServer _server;

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingRequestHandler"/> class and
        /// establishes a connection over a WebSocket to a streaming channel.
        /// </summary>
        /// <param name="logger">A logger.</param>
        /// <param name="adapter">The adapter for use when processing incoming requests.</param>
        /// <param name="socket">The base socket to use when connecting to the channel.</param>
        public StreamingRequestHandler(ILogger logger, DirectLineAdapter adapter, WebSocket socket)
        {
            _logger = logger ?? NullLogger.Instance;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            _conversations = new SortedSet<string>();
            _userAgent = GetUserAgent();
            _server = new WebSocketServer(socket, this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingRequestHandler"/> class and
        /// establishes a connection over a Named Pipe to a streaming channel.
        /// </summary>
        /// <param name="logger">A logger.</param>
        /// <param name="adapter">The adapter for use when processing incoming requests.</param>
        /// <param name="pipeName">The name of the Named Pipe to use when connecting to the channel.</param>
        public StreamingRequestHandler(ILogger logger, DirectLineAdapter adapter, string pipeName)
        {
            _logger = logger ?? NullLogger.Instance;
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentNullException(nameof(pipeName));
            }

            _conversations = new SortedSet<string>();
            _userAgent = GetUserAgent();
            _server = new NamedPipeServer(pipeName, this);
        }

        /// <summary>
        /// Gets the URL of the channel endpoint this StreamingRequestHandler receives requests from.
        /// </summary>
        /// <value>
        /// The URL of the channel endpoint this StreamingRequestHandler receives requests from.
        /// </value>
        public string ServiceUrl { get; private set; }

        /// <summary>
        /// Begins listening for incoming requests over this StreamingRequestHandler's server.
        /// </summary>
        /// <returns>A task that completes once the server is no longer listening.</returns>
        public async Task StartListening()
        {
            await _server.StartAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Checks to see if the set of conversations this StreamingRequestHandler has
        /// received requests for contains the passed in conversation ID.
        /// </summary>
        /// <param name="conversationId">The ID of the conversation to check for.</param>
        /// <returns>True if the conversation ID was found in this StreamingRequestHandler's conversation set.</returns>
        public bool HasConversation(string conversationId)
        {
            return _conversations.Contains(conversationId);
        }

        public override async Task<StreamingResponse> ProcessRequestAsync(ReceiveRequest request, ILogger<RequestHandler> logger = null, object context = null, CancellationToken cancellationToken = default)
        {
            var response = new StreamingResponse();

            // We accept all POSTs regardless of path, but anything else requires special treatment.
            if (!string.Equals(request.Verb, StreamingRequest.POST, StringComparison.InvariantCultureIgnoreCase))
            {
                return HandleCustomPaths(request);
            }

            // Convert the StreamingRequest into an activity the adapter can understand.
            string body;
            try
            {
                body = request.ReadBodyAsString();
            }
            catch (Exception ex)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                _logger.LogError("Request body missing or malformed: " + ex.Message);

                return response;
            }

            try
            {
                var activity = JsonConvert.DeserializeObject<Activity>(body, SerializationSettings.DefaultDeserializationSettings);

                // All activities received by this StreamingRequestHandler will originate from the same channel, but we won't
                // know what that channel is until we've received the first request.
                if (string.IsNullOrWhiteSpace(ServiceUrl))
                {
                    ServiceUrl = activity.ServiceUrl;
                }

                _conversations.Add(activity.Conversation.Id);

                /*
                 * Any content sent as part of a StreamingRequest, including the request body
                 * and inline attachments, appear as streams added to the same collection. The first
                 * stream of any request will be the body, which is parsed and passed into this method
                 * as the first argument, 'body'. Any additional streams are inline attachents that need
                 * to be iterated over and added to the Activity as attachments to be sent to the Bot.
                 */
                if (request.Streams.Count > 1)
                {
                    var streamAttachments = new List<Attachment>();
                    for (var i = 1; i < request.Streams.Count; i++)
                    {
                        streamAttachments.Add(new Attachment() { ContentType = request.Streams[i].ContentType, Content = request.Streams[i].Stream });
                    }

                    if (activity.Attachments != null)
                    {
                        activity.Attachments = activity.Attachments.Concat(streamAttachments).ToArray();
                    }
                    else
                    {
                        activity.Attachments = streamAttachments.ToArray();
                    }
                }

                // Now that the request has been converted into an activity we can send it to the adapter.
                var adapterResponse = await _adapter.ProcessActivityForStreamingChannelAsync(activity, cancellationToken).ConfigureAwait(false);

                // Now we convert the invokeResponse returned by the adapter into a StreamingResponse we can send back to the channel.
                if (adapterResponse == null)
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                else
                {
                    response.StatusCode = adapterResponse.Status;
                    if (adapterResponse.Body != null)
                    {
                        response.SetBody(adapterResponse.Body);
                    }
                }
            }
            catch (Exception ex)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                _logger.LogError(ex.Message);
            }

            return response;
        }

        /// <summary>
        /// Converts an <see cref="Activity"/> into a <see cref="StreamingRequest"/> and sends it to the
        /// channel this StreamingRequestHandler is connected to.
        /// </summary>
        /// <param name="activity">The activity to send.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that resolves to a <see cref="ResourceResponse"/>.</returns>
        public async Task<ResourceResponse> SendActivityAsync(Activity activity, CancellationToken cancellationToken = default)
        {
            string requestPath;
            if (!string.IsNullOrWhiteSpace(activity.ReplyToId) && activity.ReplyToId.Length >= 1)
            {
                requestPath = $"/v3/conversations/{activity.Conversation?.Id}/activities/{activity.ReplyToId}";
            }
            else
            {
                requestPath = $"/v3/conversations/{activity.Conversation?.Id}/activities";
            }

            var streamAttachments = UpdateAttachmentStreams(activity);
            var request = StreamingRequest.CreatePost(requestPath);
            request.SetBody(activity);
            if (streamAttachments != null)
            {
                foreach (var attachment in streamAttachments)
                {
                    request.AddStream(attachment);
                }
            }

            try
            {
                // TODO: Add server reconnect logic if it is disconnected when attempting to send.
                var serverResponse = await _server.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (serverResponse.StatusCode == (int)HttpStatusCode.OK)
                {
                    return serverResponse.ReadBodyAsJson<ResourceResponse>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Sends a <see cref="StreamingRequest"/> to the connected streaming channel.
        /// </summary>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task that resolves to a <see cref="ReceiveResponse"/>.</returns>
        public async Task<ReceiveResponse> SendStreamingRequestAsync(StreamingRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // TODO: Add server reconnect logic if it is disconnected when attempting to send.
                var serverResponse = await _server.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (serverResponse.StatusCode == (int)HttpStatusCode.OK)
                {
                    return serverResponse.ReadBodyAsJson<ReceiveResponse>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Build and return versioning information used for telemetry, including:
        /// The Schema version is 3.1, put into the Microsoft-BotFramework header,
        /// Protocol Extension Info,
        /// The Client SDK Version
        ///  https://github.com/Microsoft/botbuilder-dotnet/blob/d342cd66d159a023ac435aec0fdf791f93118f5f/doc/UserAgents.md,
        /// Additional Info.
        /// https://github.com/Microsoft/botbuilder-dotnet/blob/d342cd66d159a023ac435aec0fdf791f93118f5f/doc/UserAgents.md.
        /// </summary>
        /// <returns>A string containing versioning information.</returns>
        private static string GetUserAgent() =>
            string.Format(
                "Microsoft-BotFramework/3.1 Streaming-Extensions/1.0 BotBuilder/{0} ({1}; {2}; {3})",
                ConnectorClient.GetClientVersion(new ConnectorClient(new Uri("http://localhost"))),
                ConnectorClient.GetASPNetVersion(),
                ConnectorClient.GetOsVersion(),
                ConnectorClient.GetArchitecture());

        private IEnumerable<HttpContent> UpdateAttachmentStreams(Activity activity)
        {
            if (activity == null || activity.Attachments == null)
            {
                return null;
            }

            var streamAttachments = activity.Attachments.Where(a => a.Content is Stream);
            if (streamAttachments.Any())
            {
                activity.Attachments = activity.Attachments.Where(a => !(a.Content is Stream)).ToList();
                return streamAttachments.Select(streamAttachment =>
                {
                    var streamContent = new StreamContent(streamAttachment.Content as Stream);
                    streamContent.Headers.TryAddWithoutValidation("Content-Type", streamAttachment.ContentType);
                    return streamContent;
                });
            }

            return null;
        }

        private async void Reconnect(IDictionary<string, string> requestHeaders = null)
        {
            // TODO: Need to get authentication headers from the httpClient on the adapter.
            var clientWebSocket = new ClientWebSocket();
            if (requestHeaders != null)
            {
                foreach (var key in requestHeaders.Keys)
                {
                    clientWebSocket.Options.SetRequestHeader(key, requestHeaders[key]);
                }
            }

            await clientWebSocket.ConnectAsync(new Uri(ServiceUrl + _reconnectPath), CancellationToken.None).ConfigureAwait(false);
            _server = new WebSocketServer(clientWebSocket, this);
        }

        /// <summary>
        /// Checks the validity of the request and attempts to map it the correct custom endpoint,
        /// then generates and returns a response if appropriate.
        /// </summary>
        /// <param name="request">A ReceiveRequest from the connected channel.</param>
        /// <returns>A response if the given request matches against a defined path.</returns>
        private StreamingResponse HandleCustomPaths(ReceiveRequest request)
        {
            var response = new StreamingResponse();

            if (request == null || string.IsNullOrEmpty(request.Verb) || string.IsNullOrEmpty(request.Path))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                _logger.LogError("Request missing verb and/or path.");

                return response;
            }

            if (string.Equals(request.Verb, StreamingRequest.GET, StringComparison.InvariantCultureIgnoreCase) &&
                         string.Equals(request.Path, "/api/version", StringComparison.InvariantCultureIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                response.SetBody(new VersionInfo() { UserAgent = _userAgent });

                return response;
            }

            return null;
        }
    }
}
