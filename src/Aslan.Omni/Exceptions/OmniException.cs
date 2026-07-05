namespace Aslan.Omni.Exceptions;

public enum OmniErrorCode
{
    /// <summary>Token was rejected by the terminal (401). Re-pair required.</summary>
    Unauthorized,

    /// <summary>Terminal unreachable or returned 404.</summary>
    NotFound,

    /// <summary>Duplicate Ref already in-progress on the terminal (409).</summary>
    Conflict,

    /// <summary>Pairing failed — bad PIN, expired session, or rate-limited.</summary>
    PairingFailed,

    /// <summary>Payment exceeded 90-second terminal timeout.</summary>
    PaymentTimeout,

    /// <summary>HTTP or TCP error communicating with the terminal.</summary>
    NetworkError,

    /// <summary>Terminal returned an unexpected response shape.</summary>
    ProtocolError,
}

public class OmniException : Exception
{
    public OmniErrorCode Code { get; }

    public int? HttpStatus { get; }

    public OmniException(OmniErrorCode code, string message, int? httpStatus = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
    }
}
