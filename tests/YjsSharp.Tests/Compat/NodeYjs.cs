using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YjsSharp.Tests.Compat;

/// <summary>
/// A live Yjs process (tests/fixtures-generator/runner.mjs) driven over stdin/stdout, one JSON line each way.
/// </summary>
internal sealed class NodeYjs : IDisposable
{
    private readonly Process _process;

    private NodeYjs(Process process) => _process = process;

    /// <summary>Starts the runner, or marks the calling test inconclusive when node or the Yjs packages are missing.</summary>
    public static NodeYjs StartOrInconclusive()
    {
        var generator = FindGeneratorDirectory();
        if (generator == null)
        {
            Assert.Inconclusive("tests/fixtures-generator was not found above " + AppContext.BaseDirectory);
        }
        if (!Directory.Exists(Path.Combine(generator, "node_modules", "yjs")))
        {
            Assert.Inconclusive("Yjs is not installed: run `npm ci` in tests/fixtures-generator to enable the live round-trip tests");
        }
        var psi = new ProcessStartInfo("node", "runner.mjs")
        {
            WorkingDirectory       = generator,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            Assert.Inconclusive("node is not on the PATH: the live round-trip tests need node 18 or later");
            throw;
        }
        if (process == null)
        {
            Assert.Inconclusive("node could not be started");
        }
        process.StandardInput.AutoFlush = true;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new NodeYjs(process);
    }

    private static string? FindGeneratorDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("YJSSHARP_FIXTURES_GENERATOR");
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(Path.Combine(configured, "runner.mjs")) ? configured : null;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures-generator");
            if (File.Exists(Path.Combine(candidate, "runner.mjs")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Sends one command and returns its result; a failing command fails the test with the JS stack.</summary>
    public JsonNode? Call(JsonObject command)
    {
        _process.StandardInput.WriteLine(command.ToJsonString());
        var line = _process.StandardOutput.ReadLine();
        if (line == null)
        {
            Assert.Fail("the Yjs runner exited unexpectedly");
        }
        var answer = JsonNode.Parse(line)!;
        if (!(bool)answer["ok"]!)
        {
            Assert.Fail("Yjs runner error for " + command["cmd"] + ": " + (string?)answer["error"]);
        }
        return answer["result"];
    }

    public void NewDoc(string name, ulong clientId, bool gc = true)
        => Call(new JsonObject { ["cmd"] = "newDoc", ["doc"] = name, ["clientId"] = clientId, ["gc"] = gc });

    public void Apply(string name, byte[] update, bool v2 = false)
        => Call(new JsonObject { ["cmd"] = "apply", ["doc"] = name, ["update"] = Convert.ToBase64String(update), ["v2"] = v2 });

    /// <summary>Runs a script operation on a JS document and returns the v1 updates it emitted.</summary>
    public byte[][] Op(string name, JsonObject roots, JsonObject op)
    {
        var result = Call(new JsonObject { ["cmd"] = "op", ["doc"] = name, ["roots"] = roots.DeepClone(), ["op"] = op.DeepClone() })!;
        var list   = result["updates"]!.AsArray();
        var bytes  = new byte[list.Count][];
        for (var i = 0; i < list.Count; i++)
        {
            bytes[i] = Convert.FromBase64String((string)list[i]!);
        }
        return bytes;
    }

    public byte[] StateVector(string name)
        => Convert.FromBase64String((string)Call(new JsonObject { ["cmd"] = "stateVector", ["doc"] = name })!);

    public byte[] Encode(string name, byte[]? stateVector = null, bool v2 = false)
    {
        var cmd = new JsonObject { ["cmd"] = "encode", ["doc"] = name, ["v2"] = v2 };
        if (stateVector != null)
        {
            cmd["stateVector"] = Convert.ToBase64String(stateVector);
        }
        return Convert.FromBase64String((string)Call(cmd)!);
    }

    /// <summary>The canonical content and text deltas of a JS document, as <see cref="ScenarioRunner.Canonical"/> strings.</summary>
    public (string Content, string Deltas) Content(string name, JsonObject roots)
    {
        var result = Call(new JsonObject { ["cmd"] = "content", ["doc"] = name, ["roots"] = roots.DeepClone() })!;
        return (ScenarioRunner.Canonical(result["content"]), ScenarioRunner.Canonical(result["deltas"]));
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.WriteLine("{\"cmd\":\"exit\"}");
            if (!_process.WaitForExit(5000))
            {
                _process.Kill();
            }
        }
        catch (IOException)
        {
            // the runner is already gone
        }
        _process.Dispose();
    }
}
