// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;

namespace SemanticKernel.Service.CopilotChat.Skills.ChatSkills;

/// <summary>
/// A single entry in the chat memory.
/// </summary>
public class SemanticChatMemoryItem
{
    /// <summary>
    /// Label for the chat memory item.
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; }

    /// <summary>
    /// Category for the chat memory item.
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; }


    /// <summary>
    /// Details for the chat memory item.   
    /// </summary>
    [JsonPropertyName("details")]
    public string Details { get; set; }

    /// <summary>
    /// Parameterless constructor for deserialization.
    /// </summary>
    [JsonConstructor]
    public SemanticChatMemoryItem()
    {
        this.Label = string.Empty;
        this.Category = string.Empty;
        this.Details = string.Empty;
    }
    /// <summary>
    /// Create a new chat memory item.
    /// </summary>
    /// <param name="label">Label of the item.</param>
    /// <param name="category">Category or concept of the item</param>
    /// <param name="details">Details of the item.</param>
    public SemanticChatMemoryItem(string label, string category, string details)
    {
        this.Label = label;
        this.Category = category;
        this.Details = details;
    }

    /// <summary>
    /// Create a new chat memory item with an array of details.
    /// </summary>
    /// <param name="label">Label of the item.</param>
    /// <param name="category">Category or concept of the item.</param>
    /// <param name="details">Array of details for the item.</param>
    public SemanticChatMemoryItem(string label, string category, string[] details)
    {
        this.Label = label;
        this.Category = category;
        this.Details = ConvertArrayToString(details);
    }

    /// <summary>
    /// Convert a string array to a comma-separated string.
    /// </summary>
    /// <param name="array">The string array to convert.</param>
    /// <returns>A comma-separated string.</returns>
    private string ConvertArrayToString(string[] array)
    {
        return string.Join(",", array);
    }

    /// <summary>
    /// Format the chat memory item as a string.
    /// This will be used to query the vectorDB
    /// </summary>
    /// <returns>A formatted string representing the item.</returns>
    public string ToFormattedString()
    {
        return $"{this.Label}: {this.Category} : {this.Details}";
        //return $"\"label\":\"{this.Label}\",\"category\":\"{this.Category}\",\"details\":\"{this.Details}\"";
    }
}
