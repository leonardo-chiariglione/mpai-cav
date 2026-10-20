using System.Globalization;
using System.Text.RegularExpressions;

namespace Mpai.AsmStudio;

// WHAT THE USER ASKS, WHEN IT IS PLAIN: place a sound, move it, take it out, turn it. These are done as asked, at once; only what is not plain goes to the
// dialogue AIM. The numbers say where: "at 2 m x, 5 m y", "x 2 y 5", "2 m to the right and 5 m ahead", "at 2, 5". x is to the right and y ahead.
public sealed record Intent(string Verb, string Words, double? X, double? Y, double? Z, double? Yaw, bool Relative, bool HasPlace, double? Start = null, bool StartRelative = false, double? End = null, double? Height = null, double? Margin = null, double? Factor = null, double? Tail = null, string? Pattern = null, double? Db = null, double? Lufs = null, string? Field = null, double? A = null, double? B = null, string? Text = null, string? Name = null)
{
    private static readonly string Number = @"(-?\d+(?:[.,]\d+)?)";

    private static double D(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    public static Intent? Parse(string said)
    {
        var t = " " + said.ToLowerInvariant().Replace("\u2212", "-") + " ";
        var words = new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty" };
        for (var k = 0; k < words.Length; k++) t = Regex.Replace(t, @"\b" + words[k] + @"\b(?!\s+(?:of|more))", k.ToString());
        t = Regex.Replace(t, @"\b(centre|center|middle) of the (rule|ruin|rum|broom|rune|rooms)\b", "$1 of the room");   // what the recogniser writes for "room"
        t = Regex.Replace(t, @"\bprof(?:an|it|il|iles?)\b", "profile");                                                   // what the recogniser writes for "profile"
        t = Regex.Replace(t, @"(\d)\s*(decibels|decibel|db)\b", "$1 db ");
        t = Regex.Replace(t, @"(\d)\s*(metres|meters|metre|meter|m)\b", "$1 m ");
        t = Regex.Replace(t, @"(\d)\s*m([xyz])\b", "$1 m $2");           // "5my"
        t = Regex.Replace(t, @"[,;]", " ");
        t = Regex.Replace(t, @"\bminus\s+(\d)", "-$1");

        // A SPEECH FROM TEXT: "make a speech saying good morning in French called bonjour", "create an Italian speech saying buongiorno a tutti". The language is
        // the one named, else it is found out from the text; the name is the one given, else the first words.
        if (Regex.Match(said, @"\b(?:make|create|record|synthesi[sz]e|generate|new)\s+(?:me\s+)?(?:a\s+|an\s+|the\s+)?(?:new\s+)?(?:(?<lang1>english|italian|french|german|spanish|portuguese|japanese|chinese|mandarin|korean|russian|arabic)\s+)?(?:speech(?:\s+object)?|voice(?:\s+object)?|bso|spoken\s+(?:text|message))\b(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } sp)
        {
            var rest = sp.Groups["rest"].Value.Trim();
            string? name = null, lang = sp.Groups["lang1"].Success ? sp.Groups["lang1"].Value.ToLowerInvariant() : null;
            if (Regex.Match(rest, @"\s+(?:called|named)\s+(.+?)\s*[.!?]*$", RegexOptions.IgnoreCase) is { Success: true } nm) { name = nm.Groups[1].Value.Trim(' ', '"', '\''); rest = rest[..nm.Index].Trim(); }
            if (Regex.Match(rest, @"\s+in\s+(english|italian|french|german|spanish|portuguese|japanese|chinese|mandarin|korean|russian|arabic)\s*[.!?]*$", RegexOptions.IgnoreCase) is { Success: true } lm) { lang = lm.Groups[1].Value.ToLowerInvariant(); rest = rest[..lm.Index].Trim(); }
            var body = Regex.Replace(rest, @"^(?:,|:|\s)*(?:saying|that\s+says|which\s+says|says|with\s+the\s+text|of)\b[\s,:]*", "", RegexOptions.IgnoreCase).Trim(' ', ',', ':', '"', '\u201C', '\u201D', '\'');
            string? code = lang is null ? null : lang switch { "english" => "en", "italian" => "it", "french" => "fr", "german" => "de", "spanish" => "es", "portuguese" => "pt", "japanese" => "ja", "chinese" or "mandarin" => "zh", "korean" => "ko", "russian" => "ru", _ => "ar" };
            return new Intent("speech", said, null, null, null, null, false, false, Field: code, Text: body.Length > 0 ? body : null, Name: name);
        }

        // THE ACOUSTIC PROFILE, FIELD BY FIELD. "display the audio profile commands" lists them; "show the profile of the bell" and "show the scene profile" read it.
        //   the object's:  FrequencyRange, Spectrogram, Loudness, DirectionalPatterns
        //   the scene's:   Reflectivity, Reverberation, Diffusion, Absorption, Doppler
        if (Regex.IsMatch(t, @"\bprofile\b.*\bcommands?\b|\bcommands?\b.*\bprofile\b|\bprofile\s+(help|options)\b"))
            return new Intent("profilehelp", said, null, null, null, null, false, false);
        if (Regex.IsMatch(t, @"\b(show|display|what is|what's|tell me|read)\b.*\bprofile\b"))
            return new Intent(Regex.IsMatch(t, @"\bscene\b|\broom\b") ? "sceneprofile" : "profile", said, null, null, null, null, false, false);

        // FrequencyRange: "set the frequency range of bell.auo from 200 Hz to 8 kHz", "limit the bell to above 300 Hz"
        if (Regex.IsMatch(t, @"\bfrequency range\b|\bband[- ]?limit"))
        {
            var hz = Regex.Matches(t, Number + @"\s*(khz|hz)\b").Select(m => D(m.Groups[1].Value) * (m.Groups[2].Value == "khz" ? 1000 : 1)).ToList();
            double? low = null, high = null;
            if (hz.Count >= 2) { low = hz.Min(); high = hz.Max(); }
            else if (hz.Count == 1) { if (Regex.IsMatch(t, @"\b(below|under|up to|at most|maximum|max)\b")) high = hz[0]; else low = hz[0]; }
            return new Intent("frequency", said, null, null, null, null, false, false, A: low, B: high);
        }
        // Spectrogram: measured from the sound.
        if (Regex.IsMatch(t, @"\bspectrogram\b"))
            return new Intent("spectrogram", said, null, null, null, null, false, false);
        // Reflectivity: "set the early reflection time to 20 ms and the late reflection time to 80 ms"
        if (Regex.IsMatch(t, @"\b(early|late)\s+reflections?\b|\breflectivity\b"))
        {
            double? Time(string which)
            {
                var m = Regex.Match(t, @"\b" + which + @"\b[^0-9-]*?" + Number + @"\s*(ms|milliseconds?|seconds?|secs?|s)?\b");
                if (!m.Success) return null;
                var v = D(m.Groups[1].Value); var unit = m.Groups[2].Value;
                return unit is "seconds" or "second" or "secs" or "sec" or "s" ? v : v / 1000;
            }
            return new Intent("reflectivity", said, null, null, null, null, false, false, A: Time("early"), B: Time("late"));
        }
        // Diffusion, Absorption: a number each.
        if (Regex.Match(t, @"\bdiffusion\b[^0-9-]*" + Number) is { Success: true } df)
            return new Intent("diffusion", said, null, null, null, null, false, false, A: D(df.Groups[1].Value));
        if (Regex.Match(t, @"\babsorption\b[^0-9-]*" + Number) is { Success: true } ab)
            return new Intent("absorption", said, null, null, null, null, false, false, A: D(ab.Groups[1].Value));
        // Doppler: "set the Doppler direct sound factor to 0.5 and the indirect sound to 1"
        if (Regex.IsMatch(t, @"\bdoppler\b"))
        {
            double? Of(string which) => Regex.Match(t, @"\b" + which + @"\b[^0-9-]*" + Number) is { Success: true } m ? D(m.Groups[1].Value) : null;
            return new Intent("doppler", said, null, null, null, null, false, false, A: Of("direct"), B: Of("indirect"));
        }
        // Reverberation: RT60, RT30, RT20, EDT in seconds; "add reverberation" and "less reverberation" make the RT60 longer or shorter.
        if (Regex.IsMatch(t, @"\b(reverb|reverberation|reverberant|echo|echoes|rt\s?60|rt\s?30|rt\s?20|edt|early decay time|decay time)\b"))
        {
            var tail = Regex.Match(t, Number + @"\s*(?:seconds|second|secs|sec)\b");
            var kind = Regex.IsMatch(t, @"\brt\s?30\b") ? "RT30" : Regex.IsMatch(t, @"\brt\s?20\b") ? "RT20" : Regex.IsMatch(t, @"\b(edt|early decay time)\b") ? "EDT" : "RT60";
            double? factor = Regex.IsMatch(t, @"\b(remove|no|without|kill|dry)\b") ? 8 : Regex.IsMatch(t, @"\b(less|reduce|decrease|lower|shorter|weaker|down)\b") ? 2 : Regex.IsMatch(t, @"\b(much more|a lot more|lots)\b") ? 0.25 : 0.5;
            return new Intent("reverb", said, null, null, null, null, false, false, Factor: tail.Success ? null : factor, Tail: tail.Success ? D(tail.Groups[1].Value) : null, Field: kind);
        }
        // DirectionalPatterns: "make the bell cardioid", "set the directional pattern of the drum to bidirectional"
        var pattern = Regex.IsMatch(t, @"\b(bidirectional|bi-directional|figure[- ]?(?:of[- ])?8)\b") ? "bidirectional"
                    : Regex.IsMatch(t, @"\b(omnidirectional|omni-directional|omni|all directions|every direction|everywhere)\b") ? "omnidirectional"
                    : Regex.IsMatch(t, @"\b(cardioid|unidirectional|directional)\b") ? "cardioid" : null;
        if (pattern is not null) return new Intent("direction", said, null, null, null, null, false, false, Pattern: pattern);
        // Loudness: "set the loudness of the bell to -20 LUFS", "make the bell 6 dB louder", "quieter"
        if (Regex.IsMatch(t, @"\b(louder|quieter|softer|loudness|volume|lufs)\b|\d\s*db\b"))
        {
            var lufs = Regex.Match(t, Number + @"\s*lufs\b"); var db = Regex.Match(t, Number + @"\s*db\b");
            var down = Regex.IsMatch(t, @"\b(quieter|softer|lower|reduce|decrease|down|less|attenuate)\b");
            if (lufs.Success) return new Intent("loudness", said, null, null, null, null, false, false, Lufs: D(lufs.Groups[1].Value));
            var change = db.Success ? Math.Abs(D(db.Groups[1].Value)) : 3;
            return new Intent("loudness", said, null, null, null, null, false, false, Db: down ? -change : change);
        }

        // A ROOM: "build a room that encloses the two objects with a minimum distance between the walls of 3 m". The distance between walls is the height, floor to
        // ceiling; round the sides the room only encloses the objects, with a margin of one metre unless a margin is said ("with a margin of 2 m").
        if (Regex.IsMatch(t, @"\b(build|make|create|put|add|set up)\b.*\b(room|enclosure|enclose|encloses|enclosing)\b|\b(room)\b.*\b(enclose|encloses|round|around)\b"))
        {
            var margin = Regex.Match(t, @"\b(?:margin|clearance|border)\b[^0-9]*" + Number + @"\s*m\b|" + Number + @"\s*m\s*(?:margin|clearance|border)\b");
            double? roomHeight = null, edge = null;
            if (margin.Success) edge = D(margin.Groups[1].Success ? margin.Groups[1].Value : margin.Groups[2].Value);
            var tall = Regex.Match(t, @"\b(?:walls?|height|high|tall|ceiling|floor)\b[^0-9]*" + Number + @"\s*m\b|" + Number + @"\s*m\s*(?:high|tall|height)\b");
            if (tall.Success && !(margin.Success && tall.Index == margin.Index)) roomHeight = D(tall.Groups[1].Success ? tall.Groups[1].Value : tall.Groups[2].Value);
            return new Intent("room", said, null, null, null, null, false, false, Height: roomHeight, Margin: edge);
        }

        // WHEN IT SOUNDS: "start at 3 seconds", "make the bell start at 0 seconds", "start it 2 seconds later", "delay the drum by 1 second".
        // and "end at 5 seconds", "stop the drum at 4 seconds", "start at 1 second and end at 4 seconds".
        var inSeconds = Number + @"\s*(?:seconds|second|secs|sec)\b";
        var seconds = Regex.Match(t, inSeconds);
        if (seconds.Success && !Regex.IsMatch(t, @"\b(remove|delete|take out|get rid of|clear)\b"))
        {
            var starts = Regex.Match(t, @"\b(?:start|starts|begin|begins)\b[^0-9]*?" + inSeconds);
            var ends = Regex.Match(t, @"\b(?:end|ends|finish|finishes|stop|stops|cut)\b[^0-9]*?" + inSeconds);
            if (starts.Success || ends.Success)
            {
                var later = Regex.IsMatch(t, @"\b(later|more)\b"); var sooner = Regex.IsMatch(t, @"\b(earlier|sooner)\b");
                double? s = starts.Success ? D(starts.Groups[1].Value) : null; double? e = ends.Success ? D(ends.Groups[1].Value) : null;
                if (s is { } sv && sooner) s = -sv;
                var verb2 = s is not null && e is not null ? "time" : e is not null ? "end" : "start";
                return new Intent(verb2, said, null, null, null, null, false, false, s, starts.Success && (later || sooner), e);
            }
            if (Regex.IsMatch(t, @"\b(delay|later|earlier|sooner)\b"))
            {
                var n = D(seconds.Groups[1].Value); var sooner = Regex.IsMatch(t, @"\b(earlier|sooner)\b");
                return new Intent("start", said, null, null, null, null, false, false, sooner ? -n : n, true);
            }
        }

        string? verb = Regex.IsMatch(t, @"\b(remove|delete|take out|get rid of|clear)\b") ? "remove"
                 : Regex.IsMatch(t, @"\b(move|shift|push|slide|bring|take|put it|drag|relocate|raise|lift|lower)\b") ? "move"
                 : Regex.IsMatch(t, @"\b(rotate|turn|face|orient)\b") ? "turn"
                 : Regex.IsMatch(t, @"\b(place|put|add|insert|drop|set up|create|position)\b") ? "add" : null;
        if (verb is null) return null;

        double? x = null, y = null, z = null; var relative = false;

        // numbers with their axis, before or after: "2 m x", "x 2", "x = 2"
        // The axis letters either come first ("x 3 y -2") or after their numbers ("3 m x 2 m y"): the first letter, or the first number, says which.
        var letter = Regex.Match(t, @"\b[xyz]\b"); var number = Regex.Match(t, Number);
        var lettersFirst = letter.Success && number.Success && letter.Index < number.Index;
        double? Axis(string a)
        {
            var before = Regex.Match(t, Number + @"\s*(?:m\s*)?(?:on|along|in|at)?\s*(?:the\s*)?" + a + @"\b");
            var after = Regex.Match(t, @"\b" + a + @"\s*(?:=|:|at|is|of|to)?\s*" + Number);
            var m = lettersFirst ? (after.Success ? after : before) : (before.Success ? before : after);
            return m.Success ? D(m.Groups[1].Value) : null;
        }
        x = Axis("x"); y = Axis("y"); z = Axis("z");

        // distances in words: "2 m to the left", "5 m ahead"
        // The editing view is seen from above: to the right is +x and up the screen is +y, so "up" and "down" are the screen's, unless the height is meant.
        var height = Regex.IsMatch(t, @"\b(height|high|higher|elevation|elevate|raise|lift|lower|floor|ceiling)\b");
        foreach (Match m in Regex.Matches(t, Number + @"\s*m?\s*(?:to\s+the\s+|on\s+the\s+|at\s+the\s+|on\s+|to\s+)?(right|left|ahead|forward|forwards|in front|behind|back|backwards|up|down|above|below)\b"))
        {
            var n = D(m.Groups[1].Value);
            switch (m.Groups[2].Value)
            {
                case "right": x = n; relative = true; break;
                case "left": x = -n; relative = true; break;
                case "ahead" or "forward" or "forwards" or "in front": y = n; relative = true; break;
                case "behind" or "back" or "backwards": y = -n; relative = true; break;
                case "up" or "above": if (height) z = n; else y = n; relative = true; break;
                case "down" or "below": if (height) z = -n; else y = -n; relative = true; break;
            }
        }
        // "3 m horizontally" (along x) and "3 m vertically" (along y): to the right and up, unless the direction is said
        foreach (Match m in Regex.Matches(t, Number + @"\s*m?\s*(horizontally|horizontal|vertically|vertical)(?:\s+(?:to\s+the\s+)?(right|left|up|down|ahead|behind|forward|back))?\b"))
        {
            var n = D(m.Groups[1].Value); var dir = m.Groups[3].Value;
            if (m.Groups[2].Value.StartsWith("h")) x = dir == "left" ? -n : n; else y = dir is "down" or "behind" or "back" ? -n : n;
            relative = true;
        }
        // the height: "raise it 2 m", "2 m higher", "lower it by 1 m"
        if (z is null)
        {
            var up = Regex.Match(t, @"\b(?:raise|lift|elevate)\b[^0-9-]*" + Number + @"|" + Number + @"\s*m?\s*(?:higher|in height)\b");
            var down = Regex.Match(t, @"\blower\b[^0-9-]*" + Number + @"|" + Number + @"\s*m?\s*lower\b");
            if (up.Success) { z = D(up.Groups[1].Success ? up.Groups[1].Value : up.Groups[2].Value); relative = true; }
            else if (down.Success) { z = -D(down.Groups[1].Success ? down.Groups[1].Value : down.Groups[2].Value); relative = true; }
        }
        // a pair: "at 2 5", "to 2 and 5"
        if (x is null && y is null)
        {
            var pair = Regex.Match(t, @"\b(?:at|to|in|on)\s+(?:position\s+)?" + Number + @"\s*(?:m\s*)?(?:and\s+)?" + Number + @"\b");
            if (pair.Success) { x = D(pair.Groups[1].Value); y = D(pair.Groups[2].Value); relative = false; }
        }
        // "to the left" with no number: a step of one metre
        if (x is null && y is null && verb == "move")
        {
            if (Regex.IsMatch(t, @"\bto the (left)\b|\bleft\b")) { x = -1; relative = true; }
            else if (Regex.IsMatch(t, @"\bto the right\b|\bright\b")) { x = 1; relative = true; }
        }
        // "to x 2 y 3" is a place; "2 m to the left" is a step - unless it is an added sound, which is put where it is said
        double? yaw = null;
        var deg = Regex.Match(t, Number + @"\s*(?:degrees|degree|°)");
        if (deg.Success) yaw = D(deg.Groups[1].Value);
        // "to the centre of the room" is a place of its own: the middle of the room, said as a word
        var centre = Regex.IsMatch(t, @"\b(centre|center|middle)\b");
        var place = x is not null || y is not null || z is not null || (centre && verb == "move");
        // "move me", "move the listener", "turn me round": the user is moved, not a sound
        if (verb is "move" or "turn" && Regex.IsMatch(t, @"\b(me|myself|listener|my head|my position)\b") && !Regex.IsMatch(t, @"\b(face|facing|towards?)\s+me\b")) verb = "listener";
        return new Intent(verb, said, x, y, z, yaw, relative, place, Field: centre ? "center" : null);
    }
}

