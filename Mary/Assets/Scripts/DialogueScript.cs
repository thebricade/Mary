using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Plain C#, no Unity types, so the parser can be tested outside the editor.
//
// Script format (see Assets/Dialogue/_Format.txt for the full guide):
//
//   === start
//   Raoul: Christine! | I have been looking for you | everywhere tonight.
//   >> 1   | Raoul? What are you doing back here? -> surprised
//   >> 2-3 | I've been busy. -> busy
//   >> end | I'm here now. -> warm
//   -> worried
//   ~ 3.5        seconds to wait after the line
//   @ 25         speaking speed for this line, in characters per second
//
//   "|" splits what the NPC says into beats. ">>" lines are what Christine can interject, and in which beats.

/// <summary>One thing Christine can say by interrupting.</summary>
public class Interjection
{
    public string text;
    public string target;          // node to go to, or null to carry on with the node's own "->"
    public int line;               // line in the script file, for warnings

    readonly List<int[]> ranges = new List<int[]>();
    bool includesEnd;

    public void AddRange(int min, int max) { ranges.Add(new[] { min, max }); }
    public void IncludeEnd() { includesEnd = true; }

    /// <param name="beat">1 based beat of the NPC's line currently being spoken.</param>
    /// <param name="atEnd">True once the line has finished and the NPC is pausing before moving on.</param>
    public bool Matches(int beat, bool atEnd)
    {
        if (atEnd) return includesEnd;
        foreach (var r in ranges)
            if (beat >= r[0] && beat <= r[1]) return true;
        return false;
    }
}

/// <summary>One line spoken by a character, plus the ways to interrupt it.</summary>
public class DialogueNode
{
    public string id;
    public string speaker;
    public string text = "";                                   // beats joined with single spaces
    public readonly List<int> beatStarts = new List<int> { 0 };  // index in text where each beat begins
    public readonly List<Interjection> interjections = new List<Interjection>();
    public string next;                                        // where to go if nobody interjects (null = end)
    public float pause = -1f;                                  // seconds to wait after the line, -1 = default
    public float speed = -1f;                                  // characters per second, -1 = the character's own speed
    public int line;

    public int BeatCount => beatStarts.Count;

    /// <summary>The 1 based beat that contains the character at this position.</summary>
    public int BeatAt(int revealedChars)
    {
        int beat = 1;
        for (int i = 1; i < beatStarts.Count; i++)
            if (revealedChars >= beatStarts[i]) beat = i + 1;
        return beat;
    }
}

public class DialogueScript
{
    public const string End = "END";

    public readonly Dictionary<string, DialogueNode> nodes =
        new Dictionary<string, DialogueNode>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> warnings = new List<string>();
    public DialogueNode Start { get; private set; }

    public DialogueNode Get(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Equals(End, StringComparison.OrdinalIgnoreCase)) return null;
        return nodes.TryGetValue(id.Trim(), out var node) ? node : null;
    }

    // ---------- building from simple lines (older NPCs with just a list of lines) ----------

    public static DialogueScript FromLines(string speaker, IList<string> lines)
    {
        var script = new DialogueScript();
        for (int i = 0; i < lines.Count; i++)
        {
            var node = new DialogueNode { id = "line" + i, speaker = speaker, text = lines[i] ?? "" };
            node.next = i + 1 < lines.Count ? "line" + (i + 1) : null;
            script.nodes[node.id] = node;
            if (i == 0) script.Start = node;
        }
        return script;
    }

    // ---------- parsing ----------

    public static DialogueScript Parse(string source)
    {
        var script = new DialogueScript();
        DialogueNode node = null;
        string[] rows = (source ?? "").Replace("\r", "").Split('\n');

        for (int i = 0; i < rows.Length; i++)
        {
            int lineNo = i + 1;
            string row = rows[i].Trim().TrimStart('﻿');
            if (row.Length == 0 || row.StartsWith("#")) continue;

            if (row.StartsWith("==="))
            {
                string id = row.Substring(3).Trim().Trim('=').Trim();
                if (id.Length == 0) { script.Warn(lineNo, "a node needs a name after '==='"); node = null; continue; }
                if (script.nodes.ContainsKey(id)) script.Warn(lineNo, $"node '{id}' is defined twice");
                node = new DialogueNode { id = id, line = lineNo };
                script.nodes[id] = node;
                if (script.Start == null) script.Start = node;
                continue;
            }

            if (node == null) { script.Warn(lineNo, "this line is before any '=== name' node and was ignored"); continue; }

            if (row.StartsWith(">>")) script.ParseInterjection(node, row.Substring(2), lineNo);
            else if (row.StartsWith("->")) node.next = row.Substring(2).Trim();
            else if (row.StartsWith("~"))
            {
                if (float.TryParse(row.Substring(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float pause))
                    node.pause = pause;
                else script.Warn(lineNo, "expected a number of seconds after '~'");
            }
            else if (row.StartsWith("@"))
            {
                if (float.TryParse(row.Substring(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) && speed > 0f)
                    node.speed = speed;
                else script.Warn(lineNo, "expected a speed above 0 (characters per second) after '@'");
            }
            else script.ParseSpeech(node, row, lineNo);
        }

        if (script.nodes.TryGetValue("start", out var start)) script.Start = start;
        script.Validate();
        return script;
    }

    void ParseSpeech(DialogueNode node, string row, int lineNo)
    {
        int colon = row.IndexOf(':');
        if (colon <= 0) { Warn(lineNo, $"didn't understand this line: {row}"); return; }
        if (node.text.Length > 0) { Warn(lineNo, $"node '{node.id}' already has a line of speech; this one was ignored"); return; }

        node.speaker = row.Substring(0, colon).Trim();
        var sb = new StringBuilder();
        foreach (string raw in row.Substring(colon + 1).Split('|'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            if (sb.Length > 0) { sb.Append(' '); node.beatStarts.Add(sb.Length); }
            sb.Append(part);
        }
        node.text = sb.ToString();
    }

    void ParseInterjection(DialogueNode node, string body, int lineNo)
    {
        int bar = body.IndexOf('|');
        if (bar < 0) { Warn(lineNo, "an interjection needs the form  >> beats | what Christine says -> node"); return; }

        var inter = new Interjection { line = lineNo };
        foreach (string token in body.Substring(0, bar).Split(','))
            if (!ParseBeats(inter, token.Trim().ToLowerInvariant()))
                Warn(lineNo, $"didn't understand the beat '{token.Trim()}' (use 1, 2-3, end, any, or 2-end)");

        string rest = body.Substring(bar + 1);
        int arrow = rest.LastIndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            inter.text = Unquote(rest.Substring(0, arrow));
            string target = rest.Substring(arrow + 2).Trim();
            inter.target = target.Length > 0 ? target : null;
        }
        else inter.text = Unquote(rest);

        if (inter.text.Length == 0) { Warn(lineNo, "this interjection has no text"); return; }
        node.interjections.Add(inter);
    }

    static bool ParseBeats(Interjection inter, string token)
    {
        if (token == "any") { inter.AddRange(1, int.MaxValue); inter.IncludeEnd(); return true; }
        if (token == "end") { inter.IncludeEnd(); return true; }

        var range = Regex.Match(token, @"^(\d+)\s*-\s*(\d+|end)$");
        if (range.Success)
        {
            int min = int.Parse(range.Groups[1].Value);
            if (range.Groups[2].Value == "end") { inter.AddRange(min, int.MaxValue); inter.IncludeEnd(); }
            else inter.AddRange(min, int.Parse(range.Groups[2].Value));
            return true;
        }
        if (int.TryParse(token, out int single)) { inter.AddRange(single, single); return true; }
        return false;
    }

    static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '“') && (s[s.Length - 1] == '"' || s[s.Length - 1] == '”'))
            s = s.Substring(1, s.Length - 2).Trim();
        return s;
    }

    void Validate()
    {
        foreach (var n in nodes.Values)
        {
            if (string.IsNullOrEmpty(n.text)) Warn(n.line, $"node '{n.id}' has no line of speech (expected  Speaker: text)");
            CheckTarget(n.next, n.line, $"node '{n.id}'");
            foreach (var inter in n.interjections)
            {
                CheckTarget(inter.target, inter.line, $"node '{n.id}'");
                if (!inter.Matches(1, true) && !AnyBeatFits(inter, n.BeatCount))
                    Warn(inter.line, $"this interjection in '{n.id}' never appears: the line only has {n.BeatCount} beat(s)");
            }
        }
    }

    static bool AnyBeatFits(Interjection inter, int beats)
    {
        for (int b = 1; b <= beats; b++) if (inter.Matches(b, false)) return true;
        return false;
    }

    void CheckTarget(string target, int line, string where)
    {
        if (string.IsNullOrEmpty(target) || target.Equals(End, StringComparison.OrdinalIgnoreCase)) return;
        if (!nodes.ContainsKey(target)) Warn(line, $"{where} goes to '{target}', which doesn't exist");
    }

    void Warn(int line, string message) { warnings.Add($"line {line}: {message}"); }
}
