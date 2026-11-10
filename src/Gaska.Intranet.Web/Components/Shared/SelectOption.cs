namespace Gaska.Intranet.Web.Components.Shared;

/// <summary>One entry of a <see cref="SearchSelect{TValue}"/>: the stored value and the text shown.</summary>
public sealed record SelectOption<TValue>(TValue Value, string Text);
