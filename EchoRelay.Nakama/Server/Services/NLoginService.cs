﻿using EchoRelay.Core.Game;
using EchoRelay.Core.Server.Messages;
using EchoRelay.Core.Server.Messages.Common;
using EchoRelay.Core.Server.Messages.Login;
using EchoRelay.Core.Server.Services.Login;
using EchoRelay.Core.Server.Storage.Types;
using EchoRelay.Core.Utils;
using EchoRelay.Nakama;
using Jitbit.Utils;
using Nakama;
using Nakama.TinyJson;
using Newtonsoft.Json;
using Serilog;
using System.Collections.Specialized;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using ISession = Nakama.ISession;

namespace EchoRelay.Core.Server.Services.Login
{
    /// <summary>
    /// The login service is used to sign in, obtain a session, obtain logged in/other user profiles, update logged in profile, etc.
    /// </summary>
    public partial class NLoginService : NService, ILoginService
    {

        #region Fields
        /// <summary>
        /// A cache of user sessions, with expiry upon peer disconnect.
        /// </summary>
        private FastCache<Guid, XPlatformId> _userSessions;
        private readonly EchoNakama Nk;

        #endregion

        #region Constructor
        /// <summary>
        /// Initializes a new <see cref="LoginService"/> with the provided arguments.
        /// </summary>
        /// <param name="server">The server which this service is bound to.</param>
        public NLoginService(Server server, EchoNakama nk) : base(server, "LOGIN", nk)
        {
            Nk = nk;

            _userSessions = new FastCache<Guid, XPlatformId>();
            OnPeerDisconnected += LoginService_OnPeerDisconnected;
            Server.OnServerStopped += Server_OnServerStopped;
        }
        #endregion

        #region Functions
        /// <summary>
        /// Checks if a provided user session token is valid.
        /// </summary>
        /// <param name="session">The user session to verify.</param>
        /// <param name="userId">The account identifier of the user.</param>
        /// <returns>Returns true if the session for this user exists, false otherwise.</returns>
        public bool CheckUserSessionValid(Guid session, XPlatformId userId)
        {
            // If the session doesn't exist in cache and we can't obtain the associated user identifier,
            // it is not a valid session.
            if (!_userSessions.TryGet(session, out XPlatformId storedUserId))
                return false;

            // If the session exists, the user identifiers must match too.
            return userId == storedUserId;
        }

        /// <summary>
        /// Invalidates a connected peer's session token.
        /// </summary>
        /// <param name="peer">The peer to invalidate the token for.</param>
        private void InvalidatePeerUserSession(Peer peer)
        {
            // If the peer had a session token, remove it.
            Guid? session = peer.GetSessionData<Guid?>();
            if (session != null)
            {
                _userSessions.Remove(session.Value);
            }
            peer.ClearSessionData();
        }

        /// <summary>
        /// An event handler triggered when a peer disconnects from the service.
        /// </summary>
        /// <param name="service">The service the peer disconnected from.</param>
        /// <param name="peer">The peer that disconnected.</param>
        private void LoginService_OnPeerDisconnected(IService service, Peer peer)
        {
            // If the peer had a session token, update its expiry time.
            Guid? session = peer.GetSessionData<Guid?>();
            if (session != null && _userSessions.TryGet(session.Value, out XPlatformId userId))
            {
                _userSessions.AddOrUpdate(session.Value, userId, Server.Settings.SessionDisconnectedTimeout);
            }
        }

        /// <summary>
        /// An event handler which fires when the server is stopped.
        /// </summary>
        /// <param name="server">The server which has stopped.</param>
        private void Server_OnServerStopped(Server server)
        {
            // Clear all sessions on server stop.
            _userSessions.Clear();
        }

        /// <summary>
        /// Handles a packet being received by a peer.
        /// This is called after all events have been fired for <see cref="OnPacketReceived"/>.
        /// </summary>
        /// <param name="sender">The peer which sent the packet.</param>
        /// <param name="packet">The packet sent by the peer.</param>
        protected override async Task HandlePacket(Peer sender, Packet packet)
        {
            // Loop for each message received in the packet
            foreach (Message message in packet)
            {
                switch (message)
                {
                    case LoginRequest loginRequest:
                        await ProcessLoginRequest(sender, loginRequest);
                        break;
                    case LoggedInUserProfileRequest loggedInUserProfileRequest:
                        await ProcessLoggedInUserProfileRequest(sender, loggedInUserProfileRequest);
                        break;
                    case DocumentRequestv2 documentRequestv2:
                        await ProcessDocumentRequestv2(sender, documentRequestv2);
                        break;
                    case ChannelInfoRequest channelInfoRequest:
                        await ProcessChannelInfoRequest(sender, channelInfoRequest);
                        break;
                    case UpdateProfile updateProfileRequest:
                        await ProcessUpdateProfile(sender, updateProfileRequest);
                        break;
                    case OtherUserProfileRequest otherUserProfileRequest:
                        await ProcessOtherUserProfileRequest(sender, otherUserProfileRequest);
                        break;
                    case UserServerProfileUpdateRequest userServerProfileUpdateRequest:
                        await ProcessUserServerProfileUpdateRequest(sender, userServerProfileUpdateRequest);
                        break;

                    // Big dumb idea, please stop and reflect upon your life choices before continuing
                    case RemoteLogSetv3 remoteLogSetv3:
                        await ProcessRemoteLogSetv3(sender, remoteLogSetv3);
                        break;
                }
            }
        }

        private async Task ProcessRemoteLogSetv3(Peer sender, RemoteLogSetv3 request)
        {
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                return;
            }

            foreach (string log in request.Logs)
            {
                try
                {
                    dynamic? logJson = JsonConvert.DeserializeObject(log);

                    if (logJson == null) continue;
                    if (logJson["message"] != "CUSTOMIZATION_METRICS_PAYLOAD") continue;
                    if (logJson["[event_type]"] != "item_equipped") continue;
                    if (logJson["[item_name]"] == null) continue;
                    string itemName = logJson["[item_name]"].ToString();

                    var regex1 = new Regex(@"^(.*?)_.*$");
                    var regex2 = new Regex(@"^rwd_(.*?)_.*$");
                    var match1 = regex1.Match(itemName);
                    var match2 = regex2.Match(itemName);

                    string itemType = match1.Groups[1].Value;
                    if (match2.Groups.Count > 1)
                    {
                        itemType = match2.Groups[1].Value;
                    }

                    if (account.Profile.Server.Loadout?.Instances?.Unified?.Slots == null) continue;
                    switch (itemType)
                    {
                        case "emote":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Emote = itemName;
                            account.Profile.Server.Loadout.Instances.Unified.Slots.SecondEmote = itemName;
                            break;
                        case "decal":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Decal = itemName;
                            account.Profile.Server.Loadout.Instances.Unified.Slots.DecalBody = itemName;
                            break;
                        case "tint":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Tint = itemName;
                            account.Profile.Server.Loadout.Instances.Unified.Slots.TintBody = itemName;
                            /*account.Profile.Server.Loadout.Instances.Unified.Slots.TintAlignmentA = itemName;
                            account.Profile.Server.Loadout.Instances.Unified.Slots.TintAlignmentB = itemName;*/
                            break;
                        case "pattern":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Pattern = itemName;
                            account.Profile.Server.Loadout.Instances.Unified.Slots.PatternBody = itemName;
                            break;
                        case "decalback":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Pip = itemName;
                            break;
                        case "chassis":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Chassis = itemName;
                            break;
                        case "bracer":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Bracer = itemName;
                            break;
                        case "booster":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Booster = itemName;
                            break;
                        case "title":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Title = itemName;
                            break;
                        case "tag":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Tag = itemName;
                            break;
                        case "banner":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Banner = itemName;
                            break;
                        case "medal":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Medal = itemName;
                            break;
                        case "goal":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.GoalFX = itemName;
                            break;
                        case "emissive":
                            account.Profile.Server.Loadout.Instances.Unified.Slots.Emissive = itemName;
                            break;
                        default:
                            break;
                    }

                    Storage.Accounts.Set(account);
                    await sender.Send(new LoggedInUserProfileSuccess(account.AccountIdentifier, account.Profile));
                    await sender.Send(new TcpConnectionUnrequireEvent());
                }
                catch (Exception e)
                {
                    continue;
                }
            }
        }

        class LinkCode
        {
            [JsonProperty("code")]
            public string? Code = null;

        }
        private async Task<LinkCode?> GetLinkCodeForXplatformId(XPlatformId userId)
        {
            try
            {
                _ = await Nk.RefreshSessionAsync();

                IApiRpc data = await Nk.Client.RpcAsync(Nk.Session, "echorelay/getDeviceLinkCode", payload: $"{{\"xplatformid\":\"{userId}\"}}");
                if (data.Payload != null)
                {
                    return JsonConvert.DeserializeObject<LinkCode>(data.Payload);
                }
            }
            catch (ApiResponseException ex)
            {
                switch (ex.StatusCode)
                {
                    case 403: // Banned Account
                        throw new Exception("Account is banned");
                }
            }
            return null;
        }

        class LoginServerResponse
        {
            [JsonProperty("xplatform_id")]
            public XPlatformId? XPlatformId { get; set; }
            [JsonProperty("session_guid")]
            public Guid SessionGuid { get; set; }
            [JsonProperty("session_token")]
            public Session? SessionToken { get; set; }
            [JsonProperty("login_settings")]
            public LoginSettingsResource? LoginSettings { get; set; }
            [JsonProperty("account_data")]
            public AccountResource? AccountData { get; set; }
        }


        class LoginRequestPayload
        {
            [JsonProperty("account_info")]
            public LoginRequest.LoginAccountInfo AccountInfo { get; set; }
            [JsonProperty("session_guid")]
            public Guid SessionGuid { get; set; }
            [JsonProperty("xplatform_id")]
            public XPlatformId XPlatformId { get; set; }
        }
        /// <summary>
        /// Processes a <see cref="LoginRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        public async Task ProcessLoginRequest(Peer sender, LoginRequest request)
        {

            // If we have existing session data for this peer's connection, invalidate it.
            // Note: The client may have multiple connections, represented as different peers.
            // This only invalidates the current connection prior to accepting a new login.
            InvalidatePeerUserSession(sender);
            AccountResource? account;
            ISession? userSession = null;

            try
            {
                var payload = JsonConvert.SerializeObject(new LoginRequestPayload()
                {
                    AccountInfo = request.AccountInfo,
                    SessionGuid = request.Session,
                    XPlatformId = request.UserId
                });
                Log.Debug("Sending request: {payload}", payload.ToJson());
                var response = await Nk.Client.RpcAsync(Nk.Session, "relay/loginrequest", payload);
                Log.Debug($"Logged in successfully:", response);
                var loginResponse = JsonConvert.DeserializeObject<LoginServerResponse>(payload);
                account = Storage.Accounts.Get(request.UserId);
                userSession = loginResponse.SessionToken;

                if (!loginResponse.XPlatformId.Equals(request.UserId))
                {
                    Log.Error($"Mismatched userId's: {loginResponse.XPlatformId} {request.UserId}");
                    await sender.Send(new LoggedInUserProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Invalid account identifier"));
                    return;
                }
            }
            catch (Exception ex)
            {
                switch (ex)
                {
                    case ApiResponseException apiException:
                        switch (apiException.StatusCode)
                        {
                            case 401: // Account does not have a valid discord link
                            case 403: // Banned Account
                            case 404: // Account Not Found
                                await sender.Send(new LoginFailure(request.UserId, (HttpStatusCode)apiException.StatusCode, ex.Message));
                                return;
                            default:
                                await sender.Send(new LoginFailure(request.UserId, HttpStatusCode.Unauthorized,
                                                                   $"Unknown server error: {ex.Message}"));
                                return;
                        }
                    default:
                        await sender.Send(new LoginFailure(request.UserId, HttpStatusCode.Unauthorized,
                                                                                              $"Error: {ex.Message}"));
                        return;
                }
            }
            Guid sessionGuid = SecureGuidGenerator.Generate();
            _userSessions.AddOrUpdate(sessionGuid, request.UserId, TimeSpan.FromDays(3000));
            sender.SetSessionData(sessionGuid);

            // Obtain the login settings
            LoginSettingsResource? loginSettings = Storage.LoginSettings.Get();

            _userSessions.AddOrUpdate(sessionGuid, request.UserId, TimeSpan.FromDays(3000));
            sender.SetSessionData(sessionGuid);

            // Set the authenticated user identifier
            sender.UpdateUserAuthentication(request.UserId, account.Profile.Server.DisplayName);

            // Send login success response.
            await sender.Send(new LoginSuccess(request.UserId, sessionGuid));
            await sender.Send(new TcpConnectionUnrequireEvent());

            // Send login settings if we were able to obtain them.
            if (loginSettings != null)
            {
                await sender.Send(new LoginSettings(loginSettings));
            }
        }
        /// <summary>
        /// Processes a <see cref="LoggedInUserProfileRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessLoggedInUserProfileRequest(Peer sender, LoggedInUserProfileRequest request)
        {
            // Verify the session details provided
            if (!CheckUserSessionValid(request.Session, request.UserId))
            {
                await sender.Send(new LoggedInUserProfileFailure(request.UserId, HttpStatusCode.Unauthorized,
                    "Invalid Session\nYour session has expired or is no longer valid."));
                return;
            }

            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                await sender.Send(new LoggedInUserProfileFailure(request.UserId, HttpStatusCode.InternalServerError,
                    "Profile Error\nUnable to load your account due to a server issue."));
                return;
            }

            // Send the account profile to the user.
            await sender.Send(new LoggedInUserProfileSuccess(request.UserId, account.Profile));
        }

        /// <summary>
        /// Processes a <see cref="OtherUserProfileRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessOtherUserProfileRequest(Peer sender, OtherUserProfileRequest request)
        {
            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                await sender.Send(new OtherUserProfileFailure(request.UserId, HttpStatusCode.InternalServerError,
                    "Profile Error\nUnable to load your account due to a server issue."));
                return;
            }

            // Send the account profile to the user.
            await sender.Send(new OtherUserProfileSuccess(request.UserId, account.Profile.Server));
        }

        /// <summary>
        /// Processes a <see cref="UserServerProfileUpdateRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessUserServerProfileUpdateRequest(Peer sender, UserServerProfileUpdateRequest request)
        {
            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                // TODO: Failure message!
                return;
            }

            // Merge the update information with the user.
            if (request.UpdateInfo.Update != null)
            {
                // Obtain the merged profile
                AccountResource.AccountServerProfile? mergedProfile = JsonUtils.MergeObjects(account.Profile.Server, request.UpdateInfo.Update);

                // Verify we have an account and the identifier didn't change (avoids overwriting another profile in storage, as it is the storage key).
                if (mergedProfile == null || mergedProfile.XPlatformId != request.UserId.ToString())
                {
                    // TODO: Send UpdateProfileFailure(?)
                    return;
                }

                // Update the server profile in the account and set it in storage.
                account.Profile.Server = mergedProfile;
                Storage.Accounts.Set(account);
            }

            // Send the account profile to the user.
            await sender.Send(new UserServerUpdateProfileSuccess(request.UserId));
        }

        /// <summary>
        /// Processes a <see cref="UpdateProfile"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessUpdateProfile(Peer sender, UpdateProfile request)
        {
            // Verify the session details provided
            if (!CheckUserSessionValid(request.Session, request.UserId))
            {
                await sender.Send(new UpdateProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Authentication failed"));
                await sender.Send(new TcpConnectionUnrequireEvent());
                return;
            }

            // Obtain the account associated with the request.
            AccountResource? account = Storage.Accounts.Get(request.UserId);
            if (account == null)
            {
                await sender.Send(new UpdateProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Failed to obtain profile"));
                await sender.Send(new TcpConnectionUnrequireEvent());
                return;
            }

            // Verify the account identifier did not change (avoids overwriting another profile in storage, as it is the storage key).
            if (request.ClientProfile.XPlatformId != request.UserId.ToString())
            {
                await sender.Send(new UpdateProfileFailure(request.UserId, HttpStatusCode.BadRequest, "Invalid account identifier"));
                await sender.Send(new TcpConnectionUnrequireEvent());
                return;
            }

            // Get the current timestamp
            ulong currentTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // TODO: For now, we just trust all the update data and merge it in. We should scrutinize it more.
            account.Profile.Client = request.ClientProfile;

            // Update the account.
            account.Profile.Server.UpdateTime = currentTimestamp;
            account.Profile.Server.ModifyTime = currentTimestamp;
            Storage.Accounts.Set(account);

            // Send the account profile to the user.
            await sender.Send(new UpdateProfileSuccess(request.UserId));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="ChannelInfoRequest"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessChannelInfoRequest(Peer sender, ChannelInfoRequest request)
        {
            // Try to obtain our channel info
            ChannelInfoResource? channelInfo = Storage.ChannelInfo.Get();
            if (channelInfo != null)
                await sender.Send(new ChannelInfoResponse(channelInfo));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }

        /// <summary>
        /// Processes a <see cref="DocumentRequestv2"/>.
        /// </summary>
        /// <param name="sender">The sender of the request.</param>
        /// <param name="request">The request contents.</param>
        private async Task ProcessDocumentRequestv2(Peer sender, DocumentRequestv2 request)
        {
            // Obtain the symbols for the document name and language.
            long? nameSymbol = SymbolCache.GetSymbol(request.Name);
            long? languageSymbol = SymbolCache.GetSymbol(request.Language);

            // If we couldn't resolve the name or language, return a failure.
            if (nameSymbol == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not resolve symbol for document name"));
                return;
            }
            if (languageSymbol == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not resolve symbol for document language"));
                return;
            }

            // Fetch the document from storage
            DocumentResource? resource = Storage.Documents.Get((request.Name, request.Language));
            if (resource == null)
            {
                await sender.Send(new DocumentFailure(1, 0, $"Could not find document"));
                return;
            }

            // Send the document in response.
            await sender.Send(new DocumentSuccess(nameSymbol.Value, resource));
            await sender.Send(new TcpConnectionUnrequireEvent());
        }
        #endregion
    }
}