namespace MatCMS.Content;

public enum FieldType
{
    Text,
    Textarea,
    RichText,
    Image,
    Url,
    Select,

    /// <summary>
    /// Several of the same options at once. Stored as ONE comma-separated string, not an array —
    /// that is the format <see cref="TagUtil"/> already reads, so a field that used to be a single
    /// Select keeps every value it ever saved, and the renderers need no change at all.
    /// </summary>
    MultiSelect,

    List
}
