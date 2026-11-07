namespace Keystone.Web;

/// <summary>
/// Marker type for shared UI strings.
/// </summary>
/// <remarks>
/// Inject <c>IStringLocalizer&lt;SharedResource&gt;</c> to reach strings in
/// <c>Resources/SharedResource.resx</c> (English, the default) and
/// <c>Resources/SharedResource.ro-RO.resx</c> (Romanian).
/// <para>
/// Page-specific strings can instead use <c>IViewLocalizer</c>, which resolves against
/// <c>Resources/Pages/{PageName}.{culture}.resx</c>.
/// </para>
/// </remarks>
public sealed class SharedResource;
