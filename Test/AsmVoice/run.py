# Run the ASM voice-command test set against a local LLM through Ollama, the answer
# forced to the action schema (structured output). Usage: python run.py <model> [out.json]
import json, sys, time, urllib.request
from cases import SCENE, CASES

MODEL = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else f"result-{MODEL.replace(':', '_')}.json"
URL = "http://127.0.0.1:11434/api/chat"

members = list(SCENE["members"])
targets = ["scene"] + members + SCENE["library"]

# The action schema: what the UA can do. Every name the model may use is enumerated.
SCHEMA = {
    "type": "object", "required": ["actions"], "additionalProperties": False,
    "properties": {"actions": {"type": "array", "minItems": 1, "maxItems": 3, "items": {
        "type": "object", "required": ["act"], "additionalProperties": False, "properties": {
            "act": {"type": "string", "enum": ["play", "stop", "add", "remove", "move", "turn", "volume", "listen_from", "undo", "save", "ask"]},
            "target": {"type": "string", "enum": targets},
            "direction": {"type": "string", "enum": ["left", "right", "front", "back", "closer", "farther", "up", "down"]},
            "amount_m": {"type": "number", "minimum": 0.1, "maximum": 20},
            "towards": {"type": "string", "enum": ["user", "away", "left", "right"]},
            "change_db": {"type": "number", "minimum": -30, "maximum": 30},
            "question": {"type": "string"}}}}}}

def where(x, y):
    side = "left" if x < -0.3 else "right" if x > 0.3 else ""
    depth = "ahead" if y > 0.3 else "behind" if y < -0.3 else ""
    return " and ".join(p for p in [f"{abs(y):g} m {depth}" if depth else "", f"{abs(x):g} m to the {side}" if side else ""] if p)

SYSTEM = f"""You turn what a user says into actions on an audio scene. Answer only with the JSON actions.

The scene "{SCENE['name']}". The user is {SCENE['user']}. Its members, from where the user is:
""" + "\n".join(f"- {n}: {where(*p)}" for n, p in SCENE["members"].items()) + f"""
Stored sounds that can be added: {', '.join(SCENE['library'])}.

Actions:
- play (target: "scene" or a member), stop
- add (target: a stored sound; optional direction, amount_m), remove (target: a member)
- move (target: a member; direction: left, right, front, back, closer, farther, up, down; amount_m)
- turn (target: a member; towards: user, away, left, right)
- volume (target: a member or "scene"; change_db: positive louder, negative quieter)
- listen_from (the user moves: target, a member to sit next to; or direction and amount_m)
- undo, save
- ask (question): when it is not clear which member is meant, when a sound named is neither a member nor stored, or when the request is not about the scene.

Conventions: left and right are the user's. Without an amount, move 1 m; "a bit", "a little", "slightly" is 0.5 m; "much", "a lot" is 2 m. Without an amount, louder or quieter is 3 dB; "a little" is 1.5 dB; "a lot", "much" is 6 dB. Recognised speech may contain errors: read words as the member or action they most likely stand for (a "fiddle" is the violin, a "keyboard" the piano, a "singer" the voice). Several requests in one sentence are several actions, in order."""

def ask(text):
    body = {"model": MODEL, "stream": False, "format": SCHEMA, "keep_alive": "30m",
            "options": {"temperature": 0, "num_ctx": 4096},
            "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": text}]}
    req = urllib.request.Request(URL, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    t = time.time()
    with urllib.request.urlopen(req, timeout=300) as r:
        out = json.loads(r.read())
    return json.loads(out["message"]["content"])["actions"], time.time() - t

def matches(exp, got):
    if exp["act"] != got.get("act"): return False
    if "target" in exp and exp["target"] != got.get("target"): return False
    for k in ("direction", "towards"):
        if k in exp and exp[k] != got.get(k): return False
    if "amount" in exp:
        a = got.get("amount_m", 1.0)            # absent: the 1 m convention
        if not exp["amount"][0] <= a <= exp["amount"][1]: return False
    if "change" in exp:
        c = got.get("change_db", None)
        if c is None or not exp["change"][0] <= c <= exp["change"][1]: return False
    return True

def right(expected, got):
    if expected[0]["act"] == "ask":
        return len(got) >= 1 and all(g.get("act") == "ask" for g in got[:1]) and got[0]["act"] == "ask"
    return len(got) == len(expected) and all(matches(e, g) for e, g in zip(expected, got))

ask("play the scene")   # loads the model; not timed
rows, ok, times = [], 0, []
for text, expected in CASES:
    try:
        got, dt = ask(text)
    except Exception as e:
        got, dt = [{"error": str(e)}], 0
    good = right(expected, got)
    ok += good; times.append(dt)
    rows.append({"request": text, "right": good, "seconds": round(dt, 2), "got": got, "expected": expected})
    print(("OK  " if good else "BAD ") + f"{dt:5.2f}s  {text}  ->  {json.dumps(got)}", flush=True)
times.sort()
summary = {"model": MODEL, "right": ok, "of": len(CASES), "share": round(ok / len(CASES), 3),
           "median_s": round(times[len(times) // 2], 2), "p90_s": round(times[int(len(times) * 0.9)], 2)}
print(json.dumps(summary))
json.dump({"summary": summary, "rows": rows}, open(OUT, "w", encoding="utf-8"), indent=1)
