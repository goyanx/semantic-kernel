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
        ""prompt"":""A chat between human and assistant to create prompts for an API like stable-diffusion ### Human: suggest a prompt for a person in a room in the ancient roman era ### Assistant: A woman in a grand room filled with ornate decorations, in the style of ancient Rome. The person is dressed in a traditional Roman toga and is standing near a marble column. The room is illuminated by the warm glow of torchlight. ### Human: As you savor Aurelia's breasts and nipples, stroking her belly lovingly, she moans softly in pleasure. Your actions are a testament to your love for her, and she responds eagerly to your touch. You then proceed to insert your tongue into her belly button, eliciting a gasp of surprise from Aurelia. But as you move down to explore her labia with your tongue, she begins to writhe and moan in ecstasy. ### Assistant: A beautiful Roman woman's breast being licked by a man, ancient roman, nipples, breastsucking, ancient roman room ### Human: suggest a prompt to create his scene '{description}' ### Assistant: Here is the prompt --> "",
        ""batch_size"": 50,
        ""n_predict"": 256,
        ""temperature"": 0.05,
        ""threads"": 4,
        ""repeat_penalty"": 1.1,
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

            var httpresp = await httpsk.PostAsync("http://192.168.254.122:8080/completion", _tempcontext);
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