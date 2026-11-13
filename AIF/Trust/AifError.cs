namespace AIF.Controller;

// Standard-style error codes, and the outcomes of M3213 3.2 (the codes of
// M3203's API Conventions: MPAI_AIF_NOT_PRODUCED, MPAI_AIF_TIMEOUT,
// MPAI_AIF_NO_SUCH_PORT, MPAI_AIF_TYPE_NOT_ACCEPTED).
public enum AifError
{
    OK = 0,
    NotInitialized,
    NotFound,
    Failed,

    // The Port is declared, and produced nothing in the run just completed. An
    // outcome, not an error.
    NotProduced,

    // The call waited as long as it was allowed to and could not complete.
    Timeout,

    // The Module declares no boundary Port of that Data Type and Port Number, in
    // that Direction.
    NoSuchPort,

    // The datum's Data Type is not among those the Port accepts.
    TypeNotAccepted,

    // The Module is not started.
    NotStarted,

    // Not allowed by the rules of the storage (M3219 3.2).
    NotAuthorised,

    // An AIM of the Module was not trusted: the Verification Pipeline refused it
    // (M3223 3.8).
    NotTrusted,

    // A buffer given to a function is smaller than what the function returns (Basic API 2.3).
    BufferTooSmall,

    // Security API (Table 3): a signature, a message authentication code, a hash or an authentication tag does not verify.
    InvalidSignature,

    // Security API (Table 3): the random generator cannot give the bytes asked.
    InsufficientEntropy,

    // Security API (Table 3): the Implementation does not offer the algorithm or the key type.
    NotSupported
}
