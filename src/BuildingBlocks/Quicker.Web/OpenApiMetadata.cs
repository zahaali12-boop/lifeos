namespace Quicker.Web;

/// <summary>
/// Declares, for the OpenAPI document only, the multipart form an endpoint reads itself (an upload). Unlike
/// <c>Accepts&lt;T&gt;()</c> it does not constrain routing, so a request with another content type still reaches the
/// handler and gets its problem details with a stable code rather than a bare 415.
/// </summary>
public sealed record MultipartFormMetadata(Type FormType);
