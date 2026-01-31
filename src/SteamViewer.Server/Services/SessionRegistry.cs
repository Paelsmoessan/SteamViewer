using System.Collections.Concurrent;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Server.Services;

/// <summary>
/// A collaboration session with multiple participants.
/// </summary>
public sealed class CollaborationSession
{
    public required string SessionCode { get; init; }
    public string? SessionName { get; init; }
    public required string OwnerId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int MaxParticipants { get; init; } = 6;

    /// <summary>All participants in this session (including owner)</summary>
    public ConcurrentDictionary<string, ParticipantInfo> Participants { get; } = new();
}

/// <summary>
/// Registry for collaboration sessions. Thread-safe.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, CollaborationSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _clientToSession = new(); // clientId -> sessionCode
    private readonly Random _random = new();
    private readonly ILogger<SessionRegistry> _logger;

    public SessionRegistry(ILogger<SessionRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new session and adds the owner as first participant.
    /// </summary>
    public (string SessionCode, CollaborationSession Session) CreateSession(string ownerId, string displayName, string? sessionName = null)
    {
        // Generate unique 6-digit code
        string sessionCode;
        do
        {
            sessionCode = _random.Next(100000, 999999).ToString();
        } while (_sessions.ContainsKey(sessionCode));

        var session = new CollaborationSession
        {
            SessionCode = sessionCode,
            SessionName = sessionName,
            OwnerId = ownerId
        };

        // Add owner as first participant
        var ownerInfo = new ParticipantInfo(ownerId, displayName, false, DateTimeOffset.UtcNow);
        session.Participants[ownerId] = ownerInfo;

        _sessions[sessionCode] = session;
        _clientToSession[ownerId] = sessionCode;

        _logger.LogInformation("Session {SessionCode} created by {OwnerId} ({DisplayName})", sessionCode, ownerId, displayName);

        return (sessionCode, session);
    }

    /// <summary>
    /// Attempts to join an existing session. Auto-accepts anyone with valid code.
    /// </summary>
    public bool TryJoinSession(string sessionCode, string clientId, string displayName, out CollaborationSession? session, out string? error)
    {
        session = null;
        error = null;

        if (!_sessions.TryGetValue(sessionCode, out session))
        {
            error = "Session not found";
            return false;
        }

        if (session.Participants.Count >= session.MaxParticipants)
        {
            error = "Session is full";
            return false;
        }

        if (session.Participants.ContainsKey(clientId))
        {
            error = "Already in session";
            return false;
        }

        // Auto-accept: anyone with valid code joins immediately
        var participant = new ParticipantInfo(clientId, displayName, false, DateTimeOffset.UtcNow);
        session.Participants[clientId] = participant;
        _clientToSession[clientId] = sessionCode;

        _logger.LogInformation("Client {ClientId} ({DisplayName}) joined session {SessionCode}", clientId, displayName, sessionCode);

        return true;
    }

    /// <summary>
    /// Removes a client from their current session.
    /// </summary>
    public (CollaborationSession? Session, bool SessionDeleted) LeaveSession(string clientId)
    {
        if (!_clientToSession.TryRemove(clientId, out var sessionCode))
        {
            return (null, false);
        }

        if (!_sessions.TryGetValue(sessionCode, out var session))
        {
            return (null, false);
        }

        session.Participants.TryRemove(clientId, out _);

        _logger.LogInformation("Client {ClientId} left session {SessionCode}", clientId, sessionCode);

        // Delete session if empty
        if (session.Participants.IsEmpty)
        {
            _sessions.TryRemove(sessionCode, out _);
            _logger.LogInformation("Session {SessionCode} deleted (empty)", sessionCode);
            return (session, true);
        }

        return (session, false);
    }

    /// <summary>
    /// Gets the session a client is in, if any.
    /// </summary>
    public CollaborationSession? GetSessionByClient(string clientId)
    {
        if (_clientToSession.TryGetValue(clientId, out var sessionCode) &&
            _sessions.TryGetValue(sessionCode, out var session))
        {
            return session;
        }
        return null;
    }

    /// <summary>
    /// Gets all participants in a session.
    /// </summary>
    public IEnumerable<ParticipantInfo> GetParticipants(string sessionCode)
    {
        if (_sessions.TryGetValue(sessionCode, out var session))
        {
            return session.Participants.Values.ToList();
        }
        return Enumerable.Empty<ParticipantInfo>();
    }

    /// <summary>
    /// Gets all participant IDs in a session except the specified one.
    /// </summary>
    public IEnumerable<string> GetOtherParticipantIds(string sessionCode, string excludeClientId)
    {
        if (_sessions.TryGetValue(sessionCode, out var session))
        {
            return session.Participants.Keys.Where(id => id != excludeClientId).ToList();
        }
        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// Updates screen share state for a participant.
    /// </summary>
    public bool SetParticipantSharing(string clientId, bool isSharing)
    {
        var session = GetSessionByClient(clientId);
        if (session == null) return false;

        if (session.Participants.TryGetValue(clientId, out var participant))
        {
            session.Participants[clientId] = participant with { IsSharing = isSharing };
            return true;
        }
        return false;
    }
}
