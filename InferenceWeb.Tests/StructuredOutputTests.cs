using System.Text;
using System.Text.Json;
using TensorSharp.Server.StreamingWriters;

namespace InferenceWeb.Tests;

public class StructuredOutputTests
{
    [Fact]
    public void PromptInjection_KeepsLeadingMessageMarkerAtOriginalContentBoundary()
    {
        const string stablePreamble = "Stable prefix.";
        var message = new ChatMessage
        {
            Role = "system",
            Content = stablePreamble + "   ",
            CacheControl = new CacheControlMarker(),
            ContentCacheBreakpoints = new List<int> { 6 },
        };

        ChatMessage injected = StructuredOutputPrompt.Apply(
            new List<ChatMessage> { message }, StructuredOutputFormat.JsonObject())[0];

        Assert.Null(injected.CacheControl);
        Assert.Equal(new[] { 6, stablePreamble.Length }, injected.ContentCacheBreakpoints);
        Assert.StartsWith(stablePreamble + "\n\n", injected.Content, StringComparison.Ordinal);
        Assert.NotNull(message.CacheControl);
    }

    [Fact]
    public void PromptInjection_ClampsTrailingWhitespaceMarkersBeforeInstruction()
    {
        const string stablePreamble = "Stable prefix.";
        string contentWithWhitespace = stablePreamble + " \t  ";
        var message = new ChatMessage
        {
            Role = "developer",
            Content = contentWithWhitespace,
            CacheControl = new CacheControlMarker(),
            ContentCacheBreakpoints = new List<int>
            {
                contentWithWhitespace.Length,
                3,
                stablePreamble.Length + 1,
                3,
            },
        };

        ChatMessage injected = StructuredOutputPrompt.Apply(
            new List<ChatMessage> { message }, StructuredOutputFormat.JsonObject())[0];

        Assert.Equal(new[] { 3, stablePreamble.Length }, injected.ContentCacheBreakpoints);
        Assert.Null(injected.CacheControl);
        Assert.Equal(
            new[] { contentWithWhitespace.Length, 3, stablePreamble.Length + 1, 3 },
            message.ContentCacheBreakpoints);
    }

    [Fact]
    public void PromptClone_PreservesCacheMarkersAndConversationMetadata()
    {
        var message = new ChatMessage
        {
            Role = "assistant",
            Content = "earlier answer",
            RawOutputTokens = new List<int> { 11, 12 },
            RawPromptTrailingWhitespace = "\n",
            CacheControl = new CacheControlMarker { Type = "ephemeral" },
            ContentCacheBreakpoints = new List<int> { 4 },
            TextFilePaths = new List<string> { "/tmp/input.txt" },
            TextFileNames = new List<string> { "input.txt" },
            ToolCallId = "call-1",
        };

        List<ChatMessage> result = StructuredOutputPrompt.Apply(
            new List<ChatMessage> { message }, StructuredOutputFormat.JsonObject());

        ChatMessage clone = result[1];
        Assert.NotSame(message, clone);
        Assert.Equal(new[] { 11, 12 }, clone.RawOutputTokens);
        Assert.Equal("\n", clone.RawPromptTrailingWhitespace);
        Assert.Equal(new[] { 4 }, clone.ContentCacheBreakpoints);
        Assert.Equal("ephemeral", clone.CacheControl?.Type);
        Assert.Equal(new[] { "/tmp/input.txt" }, clone.TextFilePaths);
        Assert.Equal(new[] { "input.txt" }, clone.TextFileNames);
        Assert.Equal("call-1", clone.ToolCallId);
    }

    // Stream a model output in many small fragments through the json_object
    // streaming filter and return the concatenation actually sent to the client.
    private static string FeedInChunks(string modelOutput, int chunkSize = 3)
    {
        var filter = new StreamingJsonObjectFilter();
        var sb = new StringBuilder();
        for (int i = 0; i < modelOutput.Length; i += chunkSize)
            sb.Append(filter.Feed(modelOutput.Substring(i, System.Math.Min(chunkSize, modelOutput.Length - i))));
        return sb.ToString();
    }

    [Fact]
    public void StreamingJsonFilterStripsCodeFencesAndTrailingTags()
    {
        // Exactly the messy shape observed live (markdown fence + a leaked
        // Gemma channel tag after the object).
        string raw = "```json\n{\n  \"name\": \"Mars\",\n  \"diameter_km\": 6779,\n  \"has_moons\": true\n}\n```<channel|>";
        string streamed = FeedInChunks(raw);

        using var doc = JsonDocument.Parse(streamed); // must be valid JSON
        Assert.Equal("Mars", doc.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("```", streamed, System.StringComparison.Ordinal);
        Assert.DoesNotContain("channel", streamed, System.StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingJsonFilterKeepsBracesInsideStrings()
    {
        string raw = "prefix {\"text\": \"a } b { c\", \"n\": 1} trailing";
        string streamed = FeedInChunks(raw, chunkSize: 1);

        using var doc = JsonDocument.Parse(streamed);
        Assert.Equal("a } b { c", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("""{"text": "a } b { c", "n": 1}""", streamed);
    }

    [Fact]
    public void StreamingJsonFilterStopsAtFirstBalancedObject()
    {
        var filter = new StreamingJsonObjectFilter();
        string emitted = filter.Feed("{\"a\":1}{\"b\":2}");
        Assert.Equal("""{"a":1}""", emitted);
        Assert.True(filter.Done);
        Assert.Equal("", filter.Feed("more text")); // nothing after close
    }


    /// <summary>
    /// The generation-prompt logic of the Qwen 3.5 GGUF template, verbatim: the
    /// closed empty block when thinking is explicitly off, the open one otherwise.
    /// </summary>
    private const string Qwen35GenerationPromptTemplate =
        "{%- for message in messages %}" +
        "{{- '<|im_start|>' + message.role + '\\n' + message.content + '<|im_end|>\\n' }}" +
        "{%- endfor %}" +
        "{%- if add_generation_prompt %}" +
        "{{- '<|im_start|>assistant\\n' }}" +
        "{%- if enable_thinking is defined and enable_thinking is false %}" +
        "{{- '<think>\\n\\n</think>\\n\\n' }}" +
        "{%- else %}" +
        "{{- '<think>\\n' }}" +
        "{%- endif %}" +
        "{%- endif %}";

    [Fact]
    public void Qwen35ThinkingOffRendersTheShippedTemplatesClosedBlock()
    {
        // The template tests `enable_thinking is defined and enable_thinking is
        // false`. With the flag left undefined when false, the ELSE branch rendered
        // thinking ON for a request that asked for it off — which is why the hardcoded
        // renderer used to be substituted, with a prompt that differed from the
        // template's from the first tool declaration on.
        var turn = new List<ChatMessage>
        {
            new() { Role = "user", Content = "What is the tallest mountain in the world?" }
        };
        string off = ChatTemplate.RenderFromGgufTemplate(
            Qwen35GenerationPromptTemplate, turn, addGenerationPrompt: true, architecture: "qwen35", enableThinking: false);
        string on = ChatTemplate.RenderFromGgufTemplate(
            Qwen35GenerationPromptTemplate, turn, addGenerationPrompt: true, architecture: "qwen35", enableThinking: true);

        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", off, StringComparison.Ordinal);
        Assert.EndsWith("<|im_start|>assistant\n<think>\n", on, StringComparison.Ordinal);
        // Everything before the generation prompt is one and the same prompt, so the
        // KV cache serves both modes of the same conversation.
        Assert.Equal(
            off[..off.IndexOf("<|im_start|>assistant", StringComparison.Ordinal)],
            on[..on.IndexOf("<|im_start|>assistant", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Qwen35HardcodedRendererPrintsToolsExactlyAsTheTemplatesToJsonDoes()
    {
        // The hardcoded renderer is the fallback for a GGUF without a template. It
        // used to pretty-print the tool declarations where the template prints them
        // compact, so the two could never share a cached prefix.
        var tools = new List<ToolFunction>
        {
            new()
            {
                Name = "shell",
                Description = "Run a command.",
                Parameters = new Dictionary<string, ToolParameter>
                {
                    ["command"] = new() { Type = "string", Description = "The command line." },
                    ["timeout"] = new() { Type = "integer", Description = "Seconds." },
                },
                Required = new List<string> { "command" },
            },
        };
        // The user text is rendered too, or the dropped-user-message guard abandons
        // the template for the hardcoded renderer and this would compare it to itself.
        const string toolsTemplate =
            "{%- for m in messages %}{{ m.content }}{%- endfor %}{{ '|SEP|' }}{%- for tool in tools %}{{ tool | tojson }}{%- endfor %}";
        string rendered = ChatTemplate.RenderFromGgufTemplate(
            toolsTemplate,
            new List<ChatMessage> { new() { Role = "user", Content = "x" } },
            addGenerationPrompt: false, architecture: null, tools: tools, enableThinking: false);
        string fromTemplate = rendered[(rendered.IndexOf("|SEP|", StringComparison.Ordinal) + 5)..];
        string hardcoded = ChatTemplate.RenderQwen35(
            new List<ChatMessage> { new() { Role = "user", Content = "x" } },
            addGenerationPrompt: true, enableThinking: false, tools: tools);

        // The hardcoded renderer opens its own system turn for the tools and prints
        // each declaration on its own line; the text of the declaration is the point.
        Assert.Contains("<tools>\n" + fromTemplate + "\n</tools>", hardcoded, StringComparison.Ordinal);
        Assert.StartsWith("{\"type\": \"function\", \"function\": {\"name\": \"shell\"", fromTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserAcceptsDocumentedChatCompletionsJsonSchemaShape()
    {
        using var body = JsonDocument.Parse("""
        {
          "response_format": {
            "type": "json_schema",
            "json_schema": {
              "name": "research_paper_extraction",
              "strict": true,
              "schema": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" }
                },
                "required": ["title"],
                "additionalProperties": false
              }
            }
          }
        }
        """);

        bool ok = OpenAIResponseFormatParser.TryParse(body.RootElement, out var format, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(format);
        Assert.Equal(StructuredOutputKind.JsonSchema, format!.Kind);
        Assert.Equal("research_paper_extraction", format.Name);
        Assert.True(format.Strict);
    }

    [Fact]
    public void JsonSchemaValidationRejectsRootAnyOfAndMissingRequired()
    {
        var format = StructuredOutputFormat.JsonSchema("bad_schema", """
        {
          "anyOf": [
            {
              "type": "object",
              "properties": {
                "answer": { "type": "string" }
              },
              "required": [],
              "additionalProperties": false
            }
          ]
        }
        """);

        var validation = StructuredOutputValidator.ValidateSchema(format);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("root schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JsonSchemaValidationRequiresStrictAndAdditionalPropertiesFalse()
    {
        var format = StructuredOutputFormat.JsonSchema("weather", """
        {
          "type": "object",
          "properties": {
            "city": { "type": "string" }
          },
          "required": ["city"]
        }
        """, strict: false);

        var validation = StructuredOutputValidator.ValidateSchema(format);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("strict", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("additionalProperties", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void JsonObjectNormalizationExtractsCodeFencedJson()
    {
        var normalized = StructuredOutputValidator.NormalizeOutput("""
        Here you go:

        ```json
        {
          "answer": 5
        }
        ```
        """, StructuredOutputFormat.JsonObject());

        Assert.True(normalized.IsValid, normalized.ErrorMessage);
        Assert.Equal("""{"answer":5}""", normalized.NormalizedContent);
    }

    [Fact]
    public void JsonSchemaNormalizationDropsExtrasFillsNullableFieldsAndPreservesSchemaOrder()
    {
        var format = StructuredOutputFormat.JsonSchema("result", """
        {
          "type": "object",
          "properties": {
            "answer": { "type": "string" },
            "optional_note": { "type": ["string", "null"] },
            "done": { "type": "boolean" }
          },
          "required": ["answer", "optional_note", "done"],
          "additionalProperties": false
        }
        """);

        var normalized = StructuredOutputValidator.NormalizeOutput("""
        {
          "done": true,
          "extra": "remove me",
          "answer": "ok"
        }
        """, format);

        Assert.True(normalized.IsValid, normalized.ErrorMessage);
        Assert.Equal("""{"answer":"ok","optional_note":null,"done":true}""", normalized.NormalizedContent);
    }

    [Fact]
    public void JsonSchemaNormalizationSupportsDefsAndAnyOf()
    {
        var format = StructuredOutputFormat.JsonSchema("container", """
        {
          "type": "object",
          "properties": {
            "item": {
              "anyOf": [
                { "$ref": "#/$defs/person" },
                {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string" }
                  },
                  "required": ["city"],
                  "additionalProperties": false
                }
              ]
            }
          },
          "$defs": {
            "person": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age": { "type": "integer" }
              },
              "required": ["name", "age"],
              "additionalProperties": false
            }
          },
          "required": ["item"],
          "additionalProperties": false
        }
        """);

        var normalized = StructuredOutputValidator.NormalizeOutput("""
        {
          "item": {
            "age": 30,
            "name": "Ada",
            "ignored": true
          }
        }
        """, format);

        Assert.True(normalized.IsValid, normalized.ErrorMessage);
        Assert.Equal("""{"item":{"name":"Ada","age":30}}""", normalized.NormalizedContent);
    }
}
