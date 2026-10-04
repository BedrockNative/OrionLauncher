using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Orion.Domain;

namespace Orion.Desktop.ViewModels;

public partial class RtxCreatorFieldViewModel : ObservableObject
{
    public RtxCreatorField Field { get; }
    public string Label => Field.Label;
    public string Description => Field.Description;
    public bool IsToggle => Field.Type == "toggle";
    public bool IsNumber => Field.Type == "slider";
    public bool IsSelect => Field.Type == "select";
    public bool IsColor => Field.Type == "color";
    public decimal Minimum => (decimal)Field.Minimum;
    public decimal Maximum => (decimal)Field.Maximum;
    public decimal Increment => (decimal)Field.Step;
    public IReadOnlyList<string> Options => Field.Options;
    [ObservableProperty] private bool toggle;
    [ObservableProperty] private decimal? number;
    [ObservableProperty] private string? choice;
    [ObservableProperty] private decimal? red;
    [ObservableProperty] private decimal? green;
    [ObservableProperty] private decimal? blue;
    [ObservableProperty] private bool visible = true;
    public RtxCreatorFieldViewModel(RtxCreatorField field)
    {
        Field = field; Reset();
    }
    public void Reset()
    {
        switch (Field.Type)
        {
            case "toggle": Toggle = Field.Default.GetBoolean(); break;
            case "slider": Number = Field.Default.GetDecimal(); break;
            case "select": Choice = Field.Default.GetString(); break;
            case "color": Red = Field.Default[0].GetDecimal(); Green = Field.Default[1].GetDecimal(); Blue = Field.Default[2].GetDecimal(); break;
        }
    }
    public JsonElement Value() => Field.Type switch
    {
        "toggle" => JsonSerializer.SerializeToElement(Toggle),
        "slider" => JsonSerializer.SerializeToElement(Number),
        "select" => JsonSerializer.SerializeToElement(Choice),
        "color" => JsonSerializer.SerializeToElement(new[] { Red, Green, Blue }),
        _ => throw new InvalidOperationException("Unsupported creator field.")
    };
}
