using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using VibeCoder.Core.AgentTools;
using VibeCoder.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    /// <summary>
    /// Regression tests for the Plan → Act mode switch: the system prompt and the set of
    /// tools sent to the provider must be rebuilt from the current mode on every run.
    /// </summary>
    [TestFixture]
    public class AgentPromptBuilderModeTests
    {
        private static AgentToolRegistry TwoToolRegistry()
        {
            var registry = new AgentToolRegistry();
            registry.Register(new AgentTool("read_files", "Read files.", "{\"path\":\"string\"}", mutates: false, (a, c) => Task.FromResult("ok")));
            registry.Register(new AgentTool("editor", "Edit files.", "{\"path\":\"string\",\"new_text\":\"string\"}", mutates: true, (a, c) => Task.FromResult("ok"), permission: AgentPermissions.EditFiles));
            return registry;
        }

        [Test]
        public void Build_JsonChat_PlanExposesOnlyReadOnlyTools()
        {
            var prompt = AgentPromptBuilder.Build(AgentMode.Plan, TwoToolRegistry(), ToolCallingMode.JsonChat, AgentPermissions.All);

            Assert.That(prompt, Does.Contain("- read_files:"));
            Assert.That(prompt, Does.Not.Contain("- editor:"));
        }

        [Test]
        public void Build_JsonChat_ActExposesMutatingTools()
        {
            var prompt = AgentPromptBuilder.Build(AgentMode.Act, TwoToolRegistry(), ToolCallingMode.JsonChat, AgentPermissions.All);

            Assert.That(prompt, Does.Contain("- read_files:"));
            Assert.That(prompt, Does.Contain("- editor:"));
        }

        [Test]
        public void Build_JsonChat_UsesModeInstructions()
        {
            var plan = AgentPromptBuilder.Build(AgentMode.Plan, TwoToolRegistry(), ToolCallingMode.JsonChat, AgentPermissions.All);
            var act = AgentPromptBuilder.Build(AgentMode.Act, TwoToolRegistry(), ToolCallingMode.JsonChat, AgentPermissions.All);

            Assert.That(plan, Does.Contain("# Mode: PLAN (read-only)"));
            Assert.That(plan, Does.Not.Contain("# Mode: ACT (full access)"));
            Assert.That(act, Does.Contain("# Mode: ACT (full access)"));
        }

        [Test]
        public void Build_Native_PlanAndActUseDifferentPrompts()
        {
            var plan = AgentPromptBuilder.Build(AgentMode.Plan, TwoToolRegistry(), ToolCallingMode.Native);
            var act = AgentPromptBuilder.Build(AgentMode.Act, TwoToolRegistry(), ToolCallingMode.Native);

            Assert.That(plan, Does.Contain("PLAN mode"));
            Assert.That(plan, Does.Not.Contain("ACT mode"));
            Assert.That(act, Does.Contain("ACT mode"));
        }
    }

    [TestFixture]
    public class AgentRunnerModeSwitchTests
    {
        private static string FinalResponse(string content)
        {
            var payload = new Dictionary<string, object>
            {
                ["choices"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = content },
                        ["finish_reason"] = "stop"
                    }
                }
            };
            return System.Text.Json.JsonSerializer.Serialize(payload);
        }

        [Test]
        public async Task NativeMode_SameRunner_PlanToAct_RebuildsSystemPromptAndTools()
        {
            var registry = new AgentToolRegistry();
            registry.Register(new AgentTool("read_files", "Read files.", "{\"path\":\"string\"}", mutates: false, (a, c) => Task.FromResult("ok")));
            registry.Register(new AgentTool("editor", "Edit files.", "{\"path\":\"string\",\"new_text\":\"string\"}", mutates: true, (a, c) => Task.FromResult("ok"), permission: AgentPermissions.EditFiles));

            var handler = new FakeHttpHandler { Responder = req => FinalResponse("done") };
            var provider = new OpenAiCompatibleProvider(new OpenAiOptions
            {
                ApiKey = "sk-test",
                Model = "m",
                ToolCallingMode = ToolCallingMode.Native
            }, new HttpClient(handler));

            var runner = new AgentRunner(
                provider,
                registry,
                mode => AgentPromptBuilder.Build(mode, registry, ToolCallingMode.Native),
                maxIterations: 4,
                toolCallingMode: ToolCallingMode.Native,
                allowedPermissions: AgentPermissions.All);

            // First turn in Plan mode: read-only tools only, plan system prompt.
            var planResult = await runner.RunAsync("go", AgentMode.Plan, CancellationToken.None);
            Assert.That(planResult.FinalText, Is.EqualTo("done"));

            var planBody = JObject.Parse(handler.LastBody);
            Assert.That(planBody["messages"][0]["content"].Value<string>(), Does.Contain("PLAN mode"));

            var planToolNames = ((JArray)planBody["tools"]).Select(t => t["function"]["name"].Value<string>()).ToList();
            Assert.That(planToolNames, Does.Contain("read_files"));
            Assert.That(planToolNames, Does.Not.Contain("editor"));

            // Same runner, second turn switched to Act mode: mutating tools must appear,
            // and the system prompt must flip to the act prompt.
            var actResult = await runner.RunAsync("go", AgentMode.Act, CancellationToken.None);
            Assert.That(actResult.FinalText, Is.EqualTo("done"));

            var actBody = JObject.Parse(handler.LastBody);
            Assert.That(actBody["messages"][0]["content"].Value<string>(), Does.Contain("ACT mode"));

            var actToolNames = ((JArray)actBody["tools"]).Select(t => t["function"]["name"].Value<string>()).ToList();
            Assert.That(actToolNames, Does.Contain("read_files"));
            Assert.That(actToolNames, Does.Contain("editor"));
        }
    }
}
