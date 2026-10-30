using System.ComponentModel.DataAnnotations;

namespace TrexonApi;

// Trexon.Data creates the table and primary key.
public sealed class Note
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Archived { get; set; }
}

// Keep request models separate: clients cannot assign the entity ID.
public sealed class SaveNote : IValidatableObject
{
    [Required, StringLength(120, MinimumLength = 3)]
    public string Title { get; init; } = "";

    [Required(AllowEmptyStrings = true), StringLength(2000)]
    public string Text { get; init; } = "";

    public bool Archived { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((Title?.Trim().Length ?? 0) < 3)
            yield return new ValidationResult("The title must contain at least three non-padding characters.", [nameof(Title)]);
    }
}
