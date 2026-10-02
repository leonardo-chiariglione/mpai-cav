# The ASM voice-command test set: a scene, and requests with the action(s) expected.
# An expected action names only what must hold: the act, its target, and - where the
# request implies them - direction, towards, the sign or size of a change. "ask" means
# the request is ambiguous or impossible and the right answer is a question.

SCENE = {
    "name": "the quartet",
    "user": "at the origin, facing ahead",
    "members": {           # position relative to the user, metres: x right, y ahead
        "violin": (-1.5, 2.0), "cello": (1.5, 2.0), "voice": (0.0, 3.0),
        "drums": (0.0, -2.0), "piano": (-3.0, 0.0),
    },
    "library": ["flute", "rain", "applause"],   # stored Objects not in the scene
}

A = lambda act, target=None, **k: dict(act=act, **({"target": target} if target else {}), **k)
ASK = [A("ask")]

CASES = [
    # transport
    ("play the scene", [A("play", "scene")]),
    ("play everything", [A("play", "scene")]),
    ("let me hear it", [A("play", "scene")]),
    ("play the violin on its own", [A("play", "violin")]),
    ("stop", [A("stop")]),
    ("that's enough, stop playing", [A("stop")]),
    # moving
    ("move the violin to the left", [A("move", "violin", direction="left", amount=(0.9, 1.1))]),
    ("move the violin a bit to the left", [A("move", "violin", direction="left", amount=(0.4, 0.6))]),
    ("shift the cello slightly right", [A("move", "cello", direction="right", amount=(0.4, 0.6))]),
    ("bring the voice closer", [A("move", "voice", direction="closer")]),
    ("bring the voice much closer", [A("move", "voice", direction="closer", amount=(1.9, 2.1))]),
    ("push the drums farther away", [A("move", "drums", direction="farther")]),
    ("move the piano two metres forward", [A("move", "piano", direction="front", amount=(1.9, 2.1))]),
    ("put the cello a little further back", [A("move", "cello", direction="back", amount=(0.4, 0.6))]),
    ("raise the voice by half a metre", [A("move", "voice", direction="up", amount=(0.4, 0.6))]),
    ("lower the piano a metre", [A("move", "piano", direction="down", amount=(0.9, 1.1))]),
    ("shift the fiddle a little to the left", [A("move", "violin", direction="left", amount=(0.4, 0.6))]),
    ("the singer should be nearer", [A("move", "voice", direction="closer")]),
    ("move the keyboard to the right by three metres", [A("move", "piano", direction="right", amount=(2.9, 3.1))]),
    # turning
    ("turn the violin towards me", [A("turn", "violin", towards="user")]),
    ("make the cello face me", [A("turn", "cello", towards="user")]),
    ("turn the voice away from me", [A("turn", "voice", towards="away")]),
    ("point the drums to the left", [A("turn", "drums", towards="left")]),
    # volume
    ("make the drums louder", [A("volume", "drums", change=(1, 30))]),
    ("the drums are too loud", [A("volume", "drums", change=(-30, -1))]),
    ("turn down the piano a lot", [A("volume", "piano", change=(-30, -5))]),
    ("a little more cello", [A("volume", "cello", change=(1, 4))]),
    ("make everything quieter", [A("volume", "scene", change=(-30, -1))]),
    ("the voice should be 6 dB louder", [A("volume", "voice", change=(5.5, 6.5))]),
    # adding, removing
    ("add the flute", [A("add", "flute")]),
    ("put the rain in the scene", [A("add", "rain")]),
    ("bring in the applause behind me", [A("add", "applause", direction="back")]),
    ("take out the drums", [A("remove", "drums")]),
    ("I don't want the piano anymore", [A("remove", "piano")]),
    ("remove the voice", [A("remove", "voice")]),
    # where the user listens from
    ("I want to listen from the stage", [A("listen_from", direction="front")]),
    ("let me sit next to the cello", [A("listen_from", "cello")]),
    ("move me two metres back", [A("listen_from", direction="back", amount=(1.9, 2.1))]),
    # draft
    ("undo that", [A("undo")]),
    ("go back one step", [A("undo")]),
    ("save the scene", [A("save")]),
    ("keep this version", [A("save")]),
    # two actions in one request
    ("move the violin to the left and play the scene", [A("move", "violin", direction="left"), A("play", "scene")]),
    ("take out the drums and add the rain", [A("remove", "drums"), A("add", "rain")]),
    ("make the voice louder and bring it closer", [A("volume", "voice", change=(1, 30)), A("move", "voice", direction="closer")]),
    # speech recognition errors
    ("move the violent to the left", [A("move", "violin", direction="left")]),
    ("play the seen", [A("play", "scene")]),
    ("make the cello quite her", [A("volume", "cello", change=(-30, -1))]),
    ("turn the drums towards me", [A("turn", "drums", towards="user")]),
    ("at the flute", [A("add", "flute")]),
    ("move the piano too metres forward", [A("move", "piano", direction="front", amount=(1.9, 2.1))]),
    ("save the sin", [A("save")]),
    # ambiguous or impossible: the answer is a question
    ("move it to the left", ASK),
    ("make it louder", ASK),
    ("add the trumpet", ASK),
    ("turn the guitar towards me", ASK),
    ("move the instrument closer", ASK),
    ("put something nice in the corner", ASK),
    ("what is the weather like", ASK),
]
