using System.Collections.Concurrent;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// State for a collaboration session with multiple participants.
/// </summary>
public sealed class CollaborationSessionState
{
    private readonly ConcurrentDictionary<string, ParticipantInfo> _participants = new();

    /// <summary>The session code (6-digit).</summary>
    public string? SessionCode { get; private set; }

    /// <summary>Optional session name.</summary>
    public string? SessionName { get; private set; }

    /// <summary>Whether we created this session (vs joined).</summary>
    public bool IsHost { get; private set; }

    /// <summary>Our display name in the session.</summary>
    public string? MyDisplayName { get; private set; }

    /// <summary>Our client ID in the session.</summary>
    public string? MyClientId { get; private set; }

    /// <summary>Whether we're currently in a session.</summary>
    public bool InSession => SessionCode != null;

    /// <summary>All participants including self.</summary>
    public IReadOnlyCollection<ParticipantInfo> Participants => _participants.Values.ToList();

    /// <summary>Participants who are currently sharing their screen.</summary>
    public IEnumerable<ParticipantInfo> SharingParticipants => _participants.Values.Where(p => p.IsSharing);

    /// <summary>Number of participants.</summary>
    public int ParticipantCount => _participants.Count;

    /// <summary>Raised when a participant joins.</summary>
    public event EventHandler<ParticipantInfo>? ParticipantJoined;

    /// <summary>Raised when a participant leaves.</summary>
    public event EventHandler<string>? ParticipantLeft;

    /// <summary>Raised when a participant's share state changes.</summary>
    public event EventHandler<(string ParticipantId, bool IsSharing)>? ParticipantShareStateChanged;

    /// <summary>
    /// Initialize state when we create a session.
    /// </summary>
    public void CreateSession(string sessionCode, string? sessionName, string myClientId, string myDisplayName)
    {
        SessionCode = sessionCode;
        SessionName = sessionName;
        IsHost = true;
        MyClientId = myClientId;
        MyDisplayName = myDisplayName;

        // Add ourselves as first participant
        var self = new ParticipantInfo(myClientId, myDisplayName, false, DateTimeOffset.UtcNow);
        _participants[myClientId] = self;
    }

    /// <summary>
    /// Initialize state when we join an existing session.
    /// </summary>
    public void JoinSession(string sessionCode, string myClientId, string myDisplayName, IEnumerable<ParticipantInfo> existingParticipants)
    {
        SessionCode = sessionCode;
        SessionName = null;
        IsHost = false;
        MyClientId = myClientId;
        MyDisplayName = myDisplayName;

        _participants.Clear();
        foreach (var participant in existingParticipants)
        {
            _participants[participant.Id] = participant;
        }
    }

    /// <summary>
    /// Add a new participant.
    /// </summary>
    public void AddParticipant(ParticipantInfo participant)
    {
        _participants[participant.Id] = participant;
        ParticipantJoined?.Invoke(this, participant);
    }

    /// <summary>
    /// Remove a participant.
    /// </summary>
    public void RemoveParticipant(string participantId)
    {
        if (_participants.TryRemove(participantId, out _))
        {
            ParticipantLeft?.Invoke(this, participantId);
        }
    }

    /// <summary>
    /// Update a participant's screen share state.
    /// </summary>
    public void SetParticipantSharing(string participantId, bool isSharing)
    {
        if (_participants.TryGetValue(participantId, out var participant))
        {
            _participants[participantId] = participant with { IsSharing = isSharing };
            ParticipantShareStateChanged?.Invoke(this, (participantId, isSharing));
        }
    }

    /// <summary>
    /// Get a specific participant.
    /// </summary>
    public ParticipantInfo? GetParticipant(string participantId)
    {
        _participants.TryGetValue(participantId, out var participant);
        return participant;
    }

    /// <summary>
    /// Get all participant IDs except self.
    /// </summary>
    public IEnumerable<string> GetOtherParticipantIds()
    {
        return _participants.Keys.Where(id => id != MyClientId);
    }

    /// <summary>
    /// Leave the session and reset state.
    /// </summary>
    public void LeaveSession()
    {
        SessionCode = null;
        SessionName = null;
        IsHost = false;
        MyDisplayName = null;
        MyClientId = null;
        _participants.Clear();
    }
}
