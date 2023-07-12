// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI;
using Microsoft.SemanticKernel.AI.ImageGeneration;
using Microsoft.SemanticKernel.CoreSkills;
using Microsoft.SemanticKernel.Orchestration;
using Microsoft.SemanticKernel.Reliability;
using Microsoft.SemanticKernel.SkillDefinition;
using Microsoft.SemanticKernel.Skills.MsGraph;
using Microsoft.SemanticKernel.Skills.MsGraph.Connectors;
using Microsoft.SemanticKernel.Skills.MsGraph.Connectors.Client;
using Microsoft.SemanticKernel.Skills.OpenAPI.Authentication;
using Newtonsoft.Json;
using SemanticKernel.Service.CopilotChat.Models;
using SemanticKernel.Service.CopilotChat.Skills.ChatSkills;
using SemanticKernel.Service.Models;
using Utils;

namespace SemanticKernel.Service.CopilotChat.Controllers;

/// <summary>
/// Controller responsible for handling chat messages and responses.
/// </summary>
[ApiController]
public class ChatController : ControllerBase, IDisposable
{
    private readonly ILogger<ChatController> _logger;
    private readonly List<IDisposable> _disposables;
    private const string ChatSkillName = "ChatSkill";
    private const string ChatFunctionName = "Chat";

    private readonly IImageGeneration _imageGeneration;

    private string jsonString = @"
    {
        ""prompt"":""A chat between human and assistant to create a prompt for an image generator ### Human:  ### Assistant: A woman in a grand room filled with ornate decorations, in the style of ancient Rome. The person is dressed in a traditional Roman toga and is standing near a marble column. The room is illuminated by the warm glow of torchlight. ### Human: As you savor Aurelia's breasts and nipples, stroking her belly lovingly, she moans softly in pleasure. Your actions are a testament to your love for her, and she responds eagerly to your touch. You then proceed to insert your tongue into her belly button, eliciting a gasp of surprise from Aurelia. But as you move down to explore her labia with your tongue, she begins to writhe and moan in ecstasy. ### Assistant: A beautiful Roman woman's breast being licked by a man, ancient roman, nipples, breastsucking, ancient roman room ### Human: Nero, the Roman Emperor, stands in the grand Senate chamber, his face etched with anger. He raises his hand, palm outstretched, in a commanding gesture of refusal. The Senators, taken aback, stare at him in surprise. The room is filled with tension, the silence broken only by Nero's resounding ""No!"". ### Assistant: A dramatic scene unfolds in the heart of ancient Rome, where an irate Emperor Nero stands defiantly in the Senate chamber. His hand is raised in a commanding gesture, his face a mask of anger. The Senators, caught off guard, look on in surprise. The chamber, grand and ornate, is filled with a palpable tension, punctuated by Nero's resounding refusal. ### Human: Nero's refusal to the Senators was a turning point in his reign. It marked a shift in his rule, where he began to assert his authority more forcefully. Can you suggest a prompt for this scene? ### Assistant: Here is the prompt --> ""A pivotal moment in ancient Rome, where Emperor Nero, in a display of defiance and authority, rejects the Senators  proposal. His raised hand and stern expression symbolize his firm stance. The surprised Senators and the grandeur of the Senate chamber add to the intensity of the scene. This moment marks a significant shift in Nero's rule, as he begins to assert his power more forcefully."" ### Human: In a dusty arena, two gladiators face each other. One is a seasoned veteran, his body scarred from countless battles. The other is a young upstart, eager to prove his worth. They circle each other, weapons at the ready, each waiting for the other to make the first move. ### Assistant: A tense standoff in a dusty gladiatorial arena. A seasoned veteran, his body marked by the scars of countless battles, faces off against a young, eager upstart. Their weapons glint under the harsh sunlight as they circle each other, each waiting for the other to make the first move. The crowd watches in anticipation, the air thick with tension. ### Human: Can you suggest a prompt for this scene? ### Assistant: Here is the prompt --> ""A high-stakes duel in a dusty gladiatorial arena. A seasoned veteran, his body a testament to his experience, faces a young, eager challenger. Their weapons ready, they circle each other, the tension palpable. The crowd watches in anticipation, awaiting the clash that is about to unfold."" ### Human: Two armies stand on opposite sides of a vast battlefield. On one side, the disciplined ranks of the Roman legions. On the other, a horde of fierce barbarian warriors. The air is filled with the sound of clashing steel and the cries of men, as the battle begins in earnest. ### Assistant: A vast battlefield is the stage for a clash of civilizations. On one side, the disciplined ranks of Roman legions, their armor gleaming under the sun. On the other, a horde of fierce barbarian warriors, their war cries echoing across the field. The air is filled with the sound of clashing steel and the cries of men as the battle begins. ### Human: Can you suggest a prompt for this scene? ### Assistant: Here is the prompt --> ""A grand battlefield where two civilizations clash. The disciplined ranks of Roman legions face off against a horde of fierce barbarian warriors. The air resonates with the sound of clashing steel and the cries of men, capturing the intensity and chaos of the battle."" ### Human: In a dark cavern, a brave hero confronts a terrifying monster. The hero, armed with a shining sword, stands resolute despite the overwhelming odds. The monster, a massive beast with glowing eyes, roars in defiance. The battle that will decide the fate of the kingdom is about to begin. ### Assistant: A dark cavern serves as the battleground for a fateful confrontation. A brave hero, armed with a shining sword, stands resolute in the face of a terrifying monster. The beast, massive and with glowing eyes, roars in defiance. The outcome of this battle will decide the fate of the kingdom. ### Human: Can you suggest a prompt for this scene? '{description}' ### Assistant: Here is the prompt --> "",
        ""batch_size"": 256,
        ""n_predict"": 158,
        ""temperature"": 0.08,
        ""threads"": 2,
        ""repeat_penalty"": 1.3,
        ""n_keep"": 0,
        ""top_k"": 40,
        ""top_p"": 0.9,
        ""mirostat_lr"": 0.100000,
        ""mirostat_ent"": 5.0,
        ""interactive"": true,
        ""stop"": [""### Human:""],
        ""exclude"":[""### Assistant:""]        
    }";
    public ChatController(ILogger<ChatController> logger)
    {
        this._logger = logger;
        this._disposables = new List<IDisposable>();
        IKernel kernel = new KernelBuilder()
            // Add your image generation service
            .WithOpenAIImageGenerationService(Env.Var("OPENAI_API_KEY"))
            // Add your chat completion service 
            .WithOpenAIChatCompletionService("gpt-3.5-turbo", Env.Var("OPENAI_API_KEY"))
            .Build();
        _imageGeneration = kernel.GetService<IImageGeneration>();


    }

    /// <summary>
    /// Invokes the chat skill to get a response from the bot.
    /// </summary>
    /// <param name="kernel">Semantic kernel obtained through dependency injection.</param>
    /// <param name="planner">Planner to use to create function sequences.</param>
    /// <param name="ask">Prompt along with its parameters.</param>
    /// <param name="openApiSkillsAuthHeaders">Authentication headers to connect to OpenAPI Skills.</param>
    /// <returns>Results containing the response from the model.</returns>
    [Authorize]
    [Route("chat")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChatAsync(
        [FromServices] IKernel kernel,
        [FromServices] CopilotChatPlanner planner,
        [FromBody] Ask ask,
        [FromHeader] OpenApiSkillsAuthHeaders openApiSkillsAuthHeaders)
    {
        this._logger.LogDebug("Chat request received.");

        // Put ask's variables in the context we will use.
        var contextVariables = new ContextVariables(ask.Input);
        foreach (var input in ask.Variables)
        {
            contextVariables.Set(input.Key, input.Value);
        }

        // Register plugins that have been enabled
        await this.RegisterPlannerSkillsAsync(planner, openApiSkillsAuthHeaders, contextVariables);

        // Get the function to invoke
        ISKFunction? function = null;
        try
        {
            function = kernel.Skills.GetFunction(ChatSkillName, ChatFunctionName);
        }
        catch (KernelException ke)
        {
            this._logger.LogError("Failed to find {0}/{1} on server: {2}", ChatSkillName, ChatFunctionName, ke);

            return this.NotFound($"Failed to find {ChatSkillName}/{ChatFunctionName} on server");
        }

        // Run the function.
        SKContext result = await kernel.RunAsync(contextVariables, function!);
        if (result.ErrorOccurred)
        {
            if (result.LastException is AIException aiException && aiException.Detail is not null)
            {
                return this.BadRequest(string.Concat(aiException.Message, " - Detail: " + aiException.Detail));
            }

            return this.BadRequest(result.LastErrorDescription);
        }
        else
        {
            this._logger.LogInformation($"ChatController.ChatAsync =================>>> Start Sending to GGML Server");

            //result.Variables.Get("content")
            using DefaultHttpRetryHandler retryHandler = new(new HttpRetryConfig(), this._logger)
            {
                InnerHandler = new HttpClientHandler() { CheckCertificateRevocationList = true }
            };

            SKContext _tempcontext = new();

            using var httpClient = new HttpClient(retryHandler)
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
            HttpSkill httpsk = new HttpSkill(httpClient);

            var tempResult = result.Result;
            tempResult = tempResult.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)[0];
            tempResult = tempResult.Replace("\"", "\\\"");
            //tempResult = tempResult.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
            var jsonStringNew = jsonString.Replace("{description}", tempResult);

            _tempcontext["body"] = jsonStringNew;
            this._logger.LogInformation($"ChatController.ChatAsync =================>>> Ongoing Sending to GGML Server {_tempcontext["body"]}");

            var httpresp = await httpsk.PostAsync("http://192.168.254.122:8069/completion", _tempcontext);
            this._logger.LogInformation($"ChatController.ChatAsync =================>>> completion: {httpresp}");
            string content = this.GetContentAndRemoveNewlines(httpresp);
            var imageurl = await this._imageGeneration.GenerateImageAsync(content, 256, 256);
            this._logger.LogInformation($"ChatController.ChatAsync =================>>> url: {imageurl}");
            result.Variables.Set("imageUrl", imageurl);

        }

        return this.Ok(new AskResult { Value = result.Result, Variables = result.Variables.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)) });
    }

    /// <summary>
    /// Register skills with the planner's kernel.
    /// </summary>
    private async Task RegisterPlannerSkillsAsync(CopilotChatPlanner planner, OpenApiSkillsAuthHeaders openApiSkillsAuthHeaders, ContextVariables variables)
    {
        // Register authenticated skills with the planner's kernel only if the request includes an auth header for the skill.

        // Klarna Shopping
        if (openApiSkillsAuthHeaders.KlarnaAuthentication != null)
        {
            // Register the Klarna shopping ChatGPT plugin with the planner's kernel.
            using DefaultHttpRetryHandler retryHandler = new(new HttpRetryConfig(), this._logger)
            {
                InnerHandler = new HttpClientHandler() { CheckCertificateRevocationList = true }
            };
            using HttpClient importHttpClient = new(retryHandler, false);
            importHttpClient.DefaultRequestHeaders.Add("User-Agent", "Microsoft.CopilotChat");
            await planner.Kernel.ImportChatGptPluginSkillFromUrlAsync("KlarnaShoppingSkill", new Uri("https://www.klarna.com/.well-known/ai-plugin.json"),
                importHttpClient);
        }

        // GitHub
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.GithubAuthentication))
        {
            this._logger.LogInformation("Enabling GitHub skill.");
            BearerAuthenticationProvider authenticationProvider = new(() => Task.FromResult(openApiSkillsAuthHeaders.GithubAuthentication));
            await planner.Kernel.ImportOpenApiSkillFromFileAsync(
                skillName: "GitHubSkill",
                filePath: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "CopilotChat", "Skills", "OpenApiSkills/GitHubSkill/openapi.json"),
                authCallback: authenticationProvider.AuthenticateRequestAsync);
        }

        // Jira
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.JiraAuthentication))
        {
            this._logger.LogInformation("Registering Jira Skill");
            var authenticationProvider = new BasicAuthenticationProvider(() => { return Task.FromResult(openApiSkillsAuthHeaders.JiraAuthentication); });
            var hasServerUrlOverride = variables.TryGetValue("jira-server-url", out string? serverUrlOverride);

            await planner.Kernel.ImportOpenApiSkillFromFileAsync(
                skillName: "JiraSkill",
                filePath: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "CopilotChat", "Skills", "OpenApiSkills/JiraSkill/openapi.json"),
                authCallback: authenticationProvider.AuthenticateRequestAsync,
                serverUrlOverride: hasServerUrlOverride ? new Uri(serverUrlOverride!) : null);
        }

        // Microsoft Graph
        if (!string.IsNullOrWhiteSpace(openApiSkillsAuthHeaders.GraphAuthentication))
        {
            this._logger.LogInformation("Enabling Microsoft Graph skill(s).");
            BearerAuthenticationProvider authenticationProvider = new(() => Task.FromResult(openApiSkillsAuthHeaders.GraphAuthentication));
            GraphServiceClient graphServiceClient = this.CreateGraphServiceClient(authenticationProvider.AuthenticateRequestAsync);

            planner.Kernel.ImportSkill(new TaskListSkill(new MicrosoftToDoConnector(graphServiceClient)), "todo");
            planner.Kernel.ImportSkill(new CalendarSkill(new OutlookCalendarConnector(graphServiceClient)), "calendar");
            planner.Kernel.ImportSkill(new EmailSkill(new OutlookMailConnector(graphServiceClient)), "email");
        }
    }

    /// <summary>
    /// Create a Microsoft Graph service client.
    /// </summary>
    /// <param name="authenticateRequestAsyncDelegate">The delegate to authenticate the request.</param>
    private GraphServiceClient CreateGraphServiceClient(AuthenticateRequestAsyncDelegate authenticateRequestAsyncDelegate)
    {
        MsGraphClientLoggingHandler graphLoggingHandler = new(this._logger);
        this._disposables.Add(graphLoggingHandler);

        IList<DelegatingHandler> graphMiddlewareHandlers =
            GraphClientFactory.CreateDefaultHandlers(new DelegateAuthenticationProvider(authenticateRequestAsyncDelegate));
        graphMiddlewareHandlers.Add(graphLoggingHandler);

        HttpClient graphHttpClient = GraphClientFactory.Create(graphMiddlewareHandlers);
        this._disposables.Add(graphHttpClient);

        GraphServiceClient graphServiceClient = new(graphHttpClient);
        return graphServiceClient;
    }

    /// <summary>
    /// Dispose of the object.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (IDisposable disposable in this._disposables)
            {
                disposable.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private string? GetContentAndRemoveNewlines(string jsonString)
    {
        var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
        if (data != null && data.ContainsKey("content"))
        {
            var content = data["content"];
            content = content.Replace("\n", " ");
            return content;
        }
        else
        {
            return null;
        }
    }
}