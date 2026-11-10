#!/usr/bin/env python3
"""
compile.py - the Application Context of CAE-ASM from its grammar.

    python compile.py                  writes ../Access/AsmApplicationContext.json
    python compile.py --check          says whether the file there is what the sources give

Sources (this folder):
    asm-grammar.json         the words of the verbs, directions and properties, the amounts, the steps: DATA (M3281)
    asm-context.base.json    everything else the JSON Interface needs of the application (conventions, answers, questions, state): not rules

The Rules are made here, never by hand: a rule is a family of sentences (a verb, a target, a direction or a place, an amount) and what they come to in CAE-UCM. A Move changes the parts of a
Spatial Attitude the sentence states - the position, the orientation or the time - and no more (M3282 UCM-1): the position is worked out from where the object is, the orientation and the time
are not read.
"""
import json, os, sys, re

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'Access', 'AsmApplicationContext.json')

G = json.load(open(os.path.join(HERE, 'asm-grammar.json'), encoding='utf-8'))
BASE = json.load(open(os.path.join(HERE, 'asm-context.base.json'), encoding='utf-8'))

# ---------------------------------------------------------------------------------------------------------------- fragments
def alt(words):
    return '(?:' + '|'.join(words) + ')'

V = {k: alt(v) for k, v in G['Verbs'].items()}
PRON = alt(G['Pronouns'])
LISTENER = alt(G['Listener'])
NOTNAME = alt(G['NotAName'])
TOWARDS = '(?:' + '|'.join(w + ' ' for w in G['TowardsWords']) + ')?'
METRES = '(?: metres?)?'
AMT = '<NUM:n>(?: an?)?' + METRES
BYAMT = '(?:by )?' + AMT
SCENE = '{scene}'
STEP = G['Steps']
DIRS = G['Directions']

def dirwords(name):
    return alt(DIRS[name]['Words'])

# the coordinates, said in the three ways people say them
XYZ_PLAIN = '<NUM:x>,? ?(?:and )?<NUM:y>,? ?(?:and )?<NUM:z>' + METRES
EQ = '(?:=|is|equals?|equal to)?'
XYZ_LABELLED = 'x ?' + EQ + ' ?<NUM:x>' + METRES + ',? ?(?:and )?y ?' + EQ + ' ?<NUM:y>' + METRES + ',? ?(?:and )?z ?' + EQ + ' ?<NUM:z>' + METRES
XY_LABELLED = 'x ?' + EQ + ' ?<NUM:x>' + METRES + ',? ?(?:and )?y ?' + EQ + ' ?<NUM:y>' + METRES
XY_PLACE = '(?:at|to|in|on) (?:position )?<NUM:x>' + METRES + ',? ?(?:and )?<NUM:y>' + METRES
IS = '(?: is| are| should be| goes| stays| must be)?'

# ---------------------------------------------------------------------------------------------------------------- commands
def att(position=None, orientation=None, time=None):
    a = {}
    if position is not None: a['Position'] = {'CartPosition': position}
    if orientation is not None: a['Orientation'] = {'Orientation': [0, 0, orientation]}
    if time is not None: a['SpatialAttitudeTime'] = time
    return a

def moved(position=None, orientation=None, time=None):
    return {'UserCommandRQ': {'MovedObjects': {'Objects': [{'ObjectID': '{name}', 'NewSpatialAttitude': att(position, orientation, time)}]}}}

def added(position):
    return {'UserCommandRQ': {'AddedObjects': {'ObjectID': SCENE, 'Objects': [{'ObjectID': '{name}', 'SpatialAttitude': {'Position': {'CartPosition': position}, 'Orientation': {'Orientation': [0, 0, 180]}}}]}}}

def seconds(start, end):
    return {'Header': 'OSD-STM-V1.5', 'SimpleTimeID': 'time', 'SimpleTimeData': [{'FlagsByte': 0, 'StartTime': start, 'EndTime': end, 'AccuracyMode': 'single', 'AccuracyPlusMinus': 0, 'TimeType': False, 'TimeUnit': '00'}]}

# ---------------------------------------------------------------------------------------------------------------- the targets: who the sentence is about
class Target:
    """How a sentence tells what it is about, and which operation it makes of it.
       Move: an object of the scene (by name, or the one spoken of last), put elsewhere.  Add: a sound kept and not yet in the scene (by name, or the one loaded last), put in."""
    def __init__(self, kind, names, implicit, add):
        self.kind, self.names, self.implicit, self.add = kind, names, implicit, add

    def extra(self):
        if self.implicit: return {'Implicit': {'name': self.implicit}}
        if self.kind == 'raw': return {'Raw': ['name']}
        return {'Names': {'name': self.names}}

    def pattern(self, verbs, tail, verb_optional=False, sep=' '):
        """verbs: a verb group; tail: what follows the target (no leading space)."""
        v = verbs
        if self.implicit:
            return '(?:(?:' + v + ') )?(?:' + PRON + ' )?' + tail
        who = '(?:the )?(?<name>(?!' + NOTNAME + '\\b).+?)' if self.kind == 'raw' else '(?:the )?(?<name>.+?)'
        if verb_optional:
            return '(?:(?:' + v + ') )?' + who + sep + tail
        return '(?:' + v + ') ' + who + ' ' + tail

MEMBER = Target('member', 'Members', None, False)
NEWEST = Target('newest', None, 'Newest', False)
STORED = Target('stored', 'Stored', None, True)
LATEST = Target('latest', None, 'Latest', True)
RAW = Target('raw', None, None, True)

def verbs_for(t):
    return V['PlaceMove'] if t is NEWEST else V['PlaceAdd'] if t is LATEST else V['Place']

def rule(t, pattern, let=None, command=None, keep=None, extra=None):
    r = {'Pattern': pattern}
    r.update(t.extra() if t else {})
    if extra: r.update(extra)
    if let: r['Let'] = let
    if keep: r['Keep'] = keep
    r['Command'] = command
    return r

# ---------------------------------------------------------------------------------------------------------------- the families
RULES = []
def emit(r):
    RULES.append(r)

# --- the listener: where the user is, which way (the author's memory: lx, ly, lg)
def listener_command():
    return {'UserCommandRQ': {'UserPoV': {'PointOfViewID': 'listener', 'CartPosition': ['{lx}', '{ly}', 0], 'Orientation': [0, 0, '{lg}']}}}

def listener():
    lv = V['Listener']
    xy = '(?:at |to )?x ?' + EQ + ' ?<NUM:x>' + METRES + ',? ?(?:and )?y ?' + EQ + ' ?<NUM:y>' + METRES
    facing = ',? ?(?:and )?(?:facing|looking|oriented|turned|with orientation)(?: at| to)? ?<NUM:g>(?: degrees?)?'
    emit({'Pattern': '(?:' + lv + ') ' + LISTENER + ' ' + xy + facing, 'Let': {'lx': 'x', 'ly': 'y', 'lg': 'g'}, 'Keep': ['lx', 'ly', 'lg'], 'Command': listener_command()})
    emit({'Pattern': '(?:' + lv + ') ' + LISTENER + ' ' + xy, 'Let': {'lx': 'x', 'ly': 'y'}, 'Keep': ['lx', 'ly'], 'Command': listener_command()})
    emit({'Pattern': '(?:' + lv + ') ' + LISTENER + '(?: (?:to|at))?(?: the)? (?:centre|center|middle)', 'Let': {'lx': '0', 'ly': '0'}, 'Keep': ['lx', 'ly'], 'Command': listener_command()})
    emit({'Pattern': 'let me (?:listen|hear)(?: from)?(?: the)? (?:centre|center|middle)', 'Let': {'lx': '0', 'ly': '0'}, 'Keep': ['lx', 'ly'], 'Command': listener_command()})
    emit({'Pattern': '(?:turn|rotate|spin) ' + LISTENER + '(?: round| around)? by <NUM:g>(?: degrees?)?', 'Let': {'lg': 'lg+g'}, 'Keep': ['lg'], 'Command': listener_command()})
    emit({'Pattern': '(?:' + V['TurnListener'] + ') ' + LISTENER + '(?: round| around)?(?: to| towards)? ?<NUM:g>(?: degrees?)?', 'Let': {'lg': 'g'}, 'Keep': ['lg'], 'Command': listener_command()})

# --- the room: its size and its height (the sizes the person gave are remembered: a, b, h)
def closed_space(x0, x1, y0, y1, h):
    return {'UserCommandRQ': {'AddedObjects': {'ObjectID': SCENE, 'Objects': []},
            'ClosedSpace': [{'Shape': [{'SimpleLocationData': [{'RightParallelepiped': {'Header': 'OSD-RPP-V1.5', 'RightParallelepipedID': 'room', 'Dimensions': '3D',
            'CartPositions': [[x0, y0, 0], [x1, y0, 0], [x0, y1, 0], [x0, y0, h]]}}]}]}]}}

def rooms():
    R = alt(G['Room']['Words'])
    make = alt(G['Room']['Make'])
    H = str(G['Room']['Height'])
    centre = '(?:,? ?(?:at|in|on) the (?:centre|center)(?: of the (?:space|room|scene))?)?'
    where = '(?: at .*| (?:centred|centered) .*| in the (?:centre|center) .*| of the (?:room|scene))?'
    size = '(?:[.]? ?(?:the )?(?:size|dimensions?) (?:is|are)?)?(?: of| that is| which is| measuring| with)? ?'
    ceiling = '(?:[,.]? ?(?:and )?(?:with |has |the )?(?:(?:a )?(?:ceiling|height)(?: height)?(?: of| at| that is| is)? ?<NUM:h>' + METRES + '(?: high)?|<NUM:h>' + METRES + ' high))'
    by = ' ?(?:by|x|times) ?'
    box = {'x0': '-a/2', 'x1': 'a/2', 'y0': '-b/2', 'y1': 'b/2'}
    cmd = closed_space('{x0}', '{x1}', '{y0}', '{y1}', '{h}')
    sq = closed_space('{x0}', '{x1}', '{x0}', '{x1}', '{h}')
    cmd2 = {'UserCommandRQ': {'AddedObjects': {'ObjectID': SCENE, 'Objects': []}, 'ClosedSpace': [{'Shape': [{'SimpleLocationData': [{'RightParallelepiped': {'Header': 'OSD-RPP-V1.5', 'RightParallelepipedID': 'room', 'Dimensions': '3D', 'CartPositions': [['{x0}', '{x0}', 0], ['{x1}', '{x0}', 0], ['{x0}', '{x1}', 0], ['{x0}', '{x0}', '{h}']]}}]}]}]}}
    # a room with a size and a ceiling, a square one with a ceiling, a size alone, a square alone, a ceiling alone
    emit({'Pattern': make + ' (?:a |the |an )?(?:square |new )?' + R + centre + ',?' + size + '<NUM:a>' + METRES + by + '<NUM:b>' + METRES + where + ceiling + centre,
          'Let': dict(box), 'Keep': ['a', 'b', 'h'], 'Command': cmd})
    emit({'Pattern': make + ' (?:a |the |an )?square ' + R + centre + ',?' + size + '<NUM:a>' + METRES + where + ceiling + centre,
          'Let': {'x0': '-a/2', 'x1': 'a/2', 'b': 'a'}, 'Keep': ['a', 'b', 'h'], 'Command': cmd2})
    for lead in ('(?:now |please )?(?:make|set|put|raise|lower)? ?the (?:ceiling|height)(?: height)?(?: of the room)?(?: to| at| of| is)? ?<NUM:h>' + METRES + '(?: high)?',
                 '(?:now |please )?(?:the )?ceiling(?: height)?(?: to| at| of| is)? ?<NUM:h>' + METRES + '(?: high)?',
                 '(?:make|set) the room <NUM:h>' + METRES + ' high'):
        emit({'Pattern': lead, 'Let': dict(box), 'Keep': ['h'], 'Command': cmd})
    emit({'Pattern': make + ' (?:a |the |an )?(?:square |new )?' + R + centre + ',?' + size + '<NUM:a>' + METRES + by + '<NUM:b>' + METRES + where,
          'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': cmd})
    emit({'Pattern': make + ' (?:a |the |an )?<NUM:a>' + METRES + by + '<NUM:b>' + METRES + ' (?:square |new )?' + R + where,
          'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': cmd})
    emit({'Pattern': '(?:make|set) the ' + R + ' (?:to |of )?<NUM:a>' + METRES + by + '<NUM:b>' + METRES + where,
          'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': cmd})
    emit({'Pattern': make + ' (?:a |the |an )?square ' + R + centre + ',?' + size + '<NUM:a>' + METRES + where,
          'Let': {'x0': '-a/2', 'x1': 'a/2', 'h': H}, 'Keep': ['a', 'b'], 'Command': cmd2})

def rooms_loose():
    R = alt(G['Room']['Words'])
    make = alt(G['Room']['Make'])
    H = str(G['Room']['Height'])
    box = {'x0': '-a/2', 'x1': 'a/2', 'y0': '-b/2', 'y1': 'b/2'}
    cmd = closed_space('{x0}', '{x1}', '{y0}', '{y1}', '{h}')
    ceil = '(?=.*(?:ceiling|height)(?: height)?(?: of| at| to| is| that is)? ?<NUM:h>)'
    emit({'Pattern': ceil + make + ' (?:a |the |an )?(?:square |new )?' + R + '.*?<NUM:a>' + METRES + ' ?(?:by|x|times) ?<NUM:b>.*', 'Let': dict(box), 'Keep': ['a', 'b', 'h'], 'Command': cmd})
    emit({'Pattern': make + ' (?:a |the |an )?(?:square |new )?' + R + '.*?<NUM:a>' + METRES + ' ?(?:by|x|times) ?<NUM:b>.*', 'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': cmd})
    emit({'Pattern': ceil + '(?:.*?)' + R + '(?: is| of| to be| should be| measures)? ?<NUM:a>' + METRES + ' ?(?:by|times) ?<NUM:b>.*', 'Let': dict(box), 'Keep': ['a', 'b', 'h'], 'Command': cmd})
    emit({'Pattern': '(?:.*?)' + R + '(?: is| of| to be| should be| measures)? ?<NUM:a>' + METRES + ' ?(?:by|times) ?<NUM:b>.*', 'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': cmd})

# --- the scenes: a new one, one kept, a name for the open one (the name is kept as the person writes it)
def scenes():
    NEW = '(?:new|fresh|empty|blank)'
    emit({'Pattern': '(?:(?:' + V['NewScene'] + ')(?: a| another)? )?' + NEW + ' (?:scene|room)(?: called| named| as| with the name)? (?<newname>.+)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'NewScene': '{newname}'}}})
    emit({'Pattern': '(?:' + V['OpenScene'] + ')(?: me)?(?: the| my)? (?:scene|room)(?: called| named)? (?<newname>.+)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'OpenedScene': '{newname}'}}})
    emit({'Pattern': '(?:' + V['OpenScene'] + ')(?: me)?(?: the| my)? (?<newname>.+?) (?:scene|room)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'OpenedScene': '{newname}'}}})
    emit({'Pattern': '(?:please )?(?:' + V['SaveScene'] + ') (?:it |this |the |my |current |that )?(?:scene|room)(?: as| to| called| named| under the name| with the name)? (?<newname>.+)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'NamedScene': '{newname}'}}})
    emit({'Pattern': '(?:please )?(?:save|store|keep|name|call|rename) (?:it|this|that|everything|all)? ?as (?<newname>.+)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'NamedScene': '{newname}'}}})

# --- where: to the centre, by a direction and an amount, to coordinates, to the listener's side
def centre():
    """to the middle of the scene (the listener's place): the object by name - one of the scene (moved), one kept (added), one just loaded the interface has not listed (added) - or the sound that waits."""
    here = alt(G['Centre'])
    place = '(?: ' + here + ')'
    for t in (MEMBER, STORED, RAW):
        verb = V['Place']
        pattern = '(?:(?:' + verb + ') )?' + ('(?:the )?(?<name>(?!' + NOTNAME + '\\b).+?)' if t is RAW else '(?:the )?(?<name>.+?)') + place
        if t is MEMBER:
            emit(rule(t, pattern, {'z': 'pos(name,2)'}, moved(position=[0, 0, '{z}'])))
        else:
            emit(rule(t, pattern, None, added([0, 0, 0])))
    emit(rule(LATEST, '(?:(?:' + V['PlaceAdd'] + ') (?:' + PRON + ' )?)?' + here, None, added([0, 0, 0])))

def displacement(mode, d, step_expr):
    """The position a move or an add ends at, by a direction d and an amount (an expression)."""
    v = DIRS[d]['Vector']
    if mode == 'Move':
        names = ['x', 'y', 'z']
        let = {}
        for i, n in enumerate(names):
            if v[i] == 0: let[n] = 'pos(name,%d)' % i
            else: let[n] = 'pos(name,%d)%s%s' % (i, '+' if v[i] > 0 else '-', step_expr)
        return let
    let = {}
    for i, n in enumerate(['px', 'py', 'pz']):
        let[n] = '0' if v[i] == 0 else ('%s' % step_expr if v[i] > 0 else '-' + step_expr)
    return let

def by_direction():
    """a direction and an amount: 'move the violin 2 metres to the right', 'put it 3 metres behind', 'pluto to the left by 1' (named, or the one spoken of last)."""
    for t in (MEMBER, STORED, LATEST, NEWEST):
        mode = 'Add' if t.add else 'Move'
        verbs = verbs_for(t)
        verbs_tail = verbs
        for d in DIRS:
            dw = dirwords(d)
            for tail in (BYAMT + ' ' + TOWARDS + dw, TOWARDS + dw + ' ' + BYAMT):
                let = displacement(mode, d, 'n')
                if mode == 'Move':
                    emit(rule(t, t.pattern(verbs, tail), let, moved(position=['{x}', '{y}', '{z}'])))
                else:
                    emit(rule(t, t.pattern(verbs_tail, tail), let, added(['{px}', '{py}', '{pz}'])))

def by_direction_bare():
    """a direction and no amount: a step (1 m for an object that is in the scene, 2 m from the listener for one that comes in)."""
    for t in (MEMBER, STORED, LATEST, NEWEST):
        mode = 'Add' if t.add else 'Move'
        verbs = verbs_for(t)
        for d in DIRS:
            dw = dirwords(d)
            step = str(STEP[mode])
            let = displacement(mode, d, step)
            tail = '(?:to the |on the |towards the |in the )?' + dw if not t.add else '(?:to the |in the |on the )?' + dw
            if mode == 'Move':
                emit(rule(t, t.pattern(verbs, tail), let, moved(position=['{x}', '{y}', '{z}'])))
            else:
                emit(rule(t, t.pattern(verbs, tail), let, added(['{px}', '{py}', '{pz}'])))

def beside_me():
    """'behind me', 'on my left', 'in front of me': beside the listener."""
    for t in (MEMBER, STORED, LATEST, NEWEST):
        mode = 'Add' if t.add else 'Move'
        verbs = verbs_for(t)
        for d, words in G['Me']['Words'].items():
            v = DIRS[d]['Vector']
            s = STEP[mode]
            let = {'px': str(v[0] * s), 'py': str(v[1] * s), 'pz': '0'}
            tail = alt(words)
            if mode == 'Move':
                emit(rule(t, t.pattern(verbs, tail), {'x': str(v[0] * s), 'y': str(v[1] * s), 'z': '0'}, moved(position=['{x}', '{y}', '{z}'])))
            else:
                emit(rule(t, t.pattern(verbs, tail), let, added(['{px}', '{py}', '{pz}'])))

def raise_lower():
    for t in (NEWEST, MEMBER):
        for verbs, sign in ((V['Raise'], '+'), (V['Lower'], '-')):
            word = 'up' if sign == '+' else 'down'
            tail = '(?:' + word + ' )?' + BYAMT
            if t.implicit:
                pattern = '(?:(?:' + verbs + ') )?(?:' + PRON + ' )?' + tail
            else:
                pattern = '(?:' + verbs + ') (?:the )?(?<name>.+?) ' + tail
            emit(rule(t, pattern, {'x': 'pos(name,0)', 'y': 'pos(name,1)', 'z': 'pos(name,2)' + sign + 'n'}, moved(position=['{x}', '{y}', '{z}'])))

def approach():
    for t in (NEWEST, MEMBER):
        for word, sign in (('(?:closer|nearer)', '-'), ('(?:farther|further)(?: away)?', '+')):
            tail = word + '(?: to me| from me)?(?: by)? ' + AMT
            if t.implicit:
                pattern = '(?:(?:' + V['Approach'] + ') )?(?:' + PRON + ' )?' + tail
            else:
                pattern = '(?:' + V['Approach'] + ') (?:the )?(?<name>.+?) ' + tail
            r = 'sqrt(pos(name,0)*pos(name,0)+pos(name,1)*pos(name,1))'
            emit(rule(t, pattern, {'r': r, 'x': 'pos(name,0)*(r%sn)/r' % sign, 'y': 'pos(name,1)*(r%sn)/r' % sign, 'z': 'pos(name,2)'}, moved(position=['{x}', '{y}', '{z}'])))

def coordinates():
    """to a place given by its coordinates: x, y and z said as numbers or as 'x 1, y 2, z 0', or x and y alone (z is 0)."""
    be = '(?:(?:is|are|should be|goes|stays|must be) )?'
    for t in (MEMBER, STORED, LATEST, NEWEST):
        verbs = verbs_for(t)
        for forms, z, place in ((XYZ_PLAIN, None, False), (XYZ_LABELLED, None, False), (XY_LABELLED, '0', False), (XY_PLACE, '0', True)):
            if t.implicit:
                pre = '(?:(?:%s) )?(?:%s )?' % (verbs, PRON)
                if place and t is NEWEST:       # 'at 2, 1' alone is the sound that waits to come in: moving one needs a verb or the pronoun
                    pre = '(?:(?:%s) (?:%s )?|%s )' % (verbs, PRON, PRON)
                pattern = pre + be + ('' if place else '(?:at |to )?') + forms
            else:
                lead = '(?:(?:%s) )?(?:the )?(?<name>(?!%s\\b).+?)' % (verbs, NOTNAME)        # 'it' is not a name: it is the sound that waits, else the one spoken of last
                pattern = lead + IS + (' ?' if place else '(?: (?:at|to))? ?') + forms
            zc = '{z}' if z is None else 0
            if t.add:
                emit(rule(t, pattern, None, added(['{x}', '{y}', zc])))
            else:
                emit(rule(t, pattern, None, moved(position=['{x}', '{y}', zc])))

def turn():
    emit(rule(MEMBER, '(?:' + V['Turn'] + ') (?:the )?(?<name>.+?) (?:to |by )?<NUM:a> degrees?', None, moved(orientation='{a}')))
    emit(rule(NEWEST, '(?:' + V['Turn'] + ') (?:' + PRON + ' )?(?:to |by )?<NUM:a> degrees?', None, moved(orientation='{a}')))
    for t in (NEWEST, MEMBER):
        face = '(?:face|look at|face towards|turn towards) me'
        if t.implicit:
            emit(rule(t, '(?:(?:make|turn|have) )?(?:' + PRON + ' )?' + face, None, moved(orientation=180)))
        else:
            emit(rule(t, '(?:make|turn|have) (?:the )?(?<name>.+?) ' + face, None, moved(orientation=180)))

def level():
    emit(rule(MEMBER, '(?:' + V['Level'] + ') (?:the )?(?:level of )?(?:the )?(?<name>.+?) (?:to |at )?<NUM:l> ?' + alt(G['Levels']['Units']), None,
              {'UserCommandRQ': {'ChangedObjects': {'Objects': [{'ObjectID': '{name}', 'Level': '{l}'}]}}}))
    emit(rule(MEMBER, '(?:' + V['Level'] + ') (?:the )?(?<name>.+?) (?:level )?(?:to |at )?<NUM:l> ?' + alt(G['Levels']['Units']), None,
              {'UserCommandRQ': {'ChangedObjects': {'Objects': [{'ObjectID': '{name}', 'Level': '{l}'}]}}}))

def remove_delete():
    emit(rule(MEMBER, '(?:' + V['Remove'] + ') (?:the )?(?<name>.+?)(?: from the scene| out of the scene)?', None,
              {'UserCommandRQ': {'RemovedObjects': {'Objects': [{'ObjectID': '{name}'}]}}}))
    emit({'Pattern': '(?:' + V['Delete'] + ') (?:the )?(?<name>.+?)(?: from the (?:store|storage|library))?', 'Names': {'name': 'Kept'},
          'Command': {'UserCommandRQ': {'DeletedObjects': {'Objects': [{'ObjectID': '{name}'}]}}}})

def rename_object():
    """"rename the guitar as piano": the name of an object that is kept, by the verb first (the same command as "set the name of the guitar to piano")."""
    emit({'Pattern': '(?:' + V['Rename'] + ') (?:the )?(?<name>.+?) (?:as|to|into)(?: be)? (?<text>.+)', 'Names': {'name': 'Kept'}, 'Raw': ['text'],
          'Command': {'UserCommandRQ': {'ChangedObjects': {'Objects': [{'ObjectID': '{name}', 'Name': '{text}'}]}}}})

def rendering():
    """how the scene is heard: "play on headphones", "hear it on stereo speakers", "set the output to 5.1" - the Rendering Mode of the Scene (a property of the Basic Audio Scene Descriptors and the Audio Scene Descriptors; the Module's)."""
    for row in G['Renderings']:
        scene = {'RenderingMode': row['Mode']}
        if 'ChannelCount' in row: scene['ChannelCount'] = row['ChannelCount']
        command = {'UserCommandRQ': {'ModifiedScene': scene}}
        words = alt(row['Words'])
        emit({'Pattern': '(?:please )?(?:(?:play|hear|listen|render|output)(?: it| the scene| the sound)?|(?:set|change|switch)(?: the)?(?: sound| audio)? (?:output|rendering|playback))'
                         '(?: (?:on|for|to|through|in|with|over))? (?:the |my )?' + words + '(?: output| rendering)?', 'Command': command})

def undo_play():
    emit({'Pattern': 'undo(?: that| the last (?:command|thing))?|go back|take that back', 'Command': {'UserCommandRQ': {'RevokedUserCommandID': '{last}'}}})
    emit({'Pattern': 'play(?: the)?(?: scene| it)?|let me hear(?: the scene| it)?', 'Command': {'UserCommandRQ': {'DeliveredObject': 'stage'}}})
    # one sound of the scene, by its name: "play clip777", "let me hear the guitar" (and "playclip777", the space left out)
    emit({'Pattern': '(?:play ?|let me hear |hear |listen to )(?:the )?(?<name>.+?)', 'Names': {'name': 'Members'}, 'Command': {'UserCommandRQ': {'DeliveredObject': '{name}'}}})

def times():
    """when an object sounds in the scene: its start and its end, in seconds (the time only: where it is, and which way it faces, are not said)."""
    s_e = '(?:[^0-9]*?)<NUM:s>(?: seconds?)?(?:[^0-9]*?)<NUM:e>(?: seconds?)?'
    e_s = '(?:[^0-9]*?)<NUM:e>(?: seconds?)?(?:[^0-9]*?)<NUM:s>(?: seconds?)?'
    cmd = moved(time=seconds('{s}', '{e}'))
    setw = '(?:' + V['Set'] + ')? ?'
    T = G['Times']
    emit(rule(MEMBER, setw + '(?:the )?' + alt(T['StartOf']) + '(?: time)? (?:of|for) (?:the )?(?<name>.+?) (?:is|at|to|should be|from|starts?|begins?)' + s_e, None, cmd))
    emit(rule(MEMBER, setw + '(?:the )?end(?: time)? (?:of|for) (?:the )?(?<name>.+?) (?:is|at|to|should be|from|starts?|begins?)' + e_s, None, cmd))
    emit(rule(MEMBER, setw + '(?:the )?(?<name>.+?)(?:\'s)? ' + alt(T['Words']) + s_e, None, cmd))
    emit(rule(MEMBER, setw + '(?:the )?(?<name>.+?)(?: lasts?| plays?| runs?| sounds?| is heard)? (?:starts?|begins?|from|between)' + s_e, None, cmd))
    emit(rule(MEMBER, '(?:set|make|change|put)? ?(?:the )?(?:duration|time|length) of (?:the )?(?<name>.+?) (?:should be|to be|is|to|from)? ?(?:start(?:ing)?|from)?(?: at)? ?<NUM:s>(?: seconds?)?(?: and| to| until|,)? ?(?:end(?:ing)?)?(?: at)? ?<NUM:e>(?: seconds?)?', None, cmd))
    emit(rule(MEMBER, '(?:set|make|change|put) (?:the )?(?<name>.+?) (?:to )?(?:start(?:ing)?|from)(?: at)? ?<NUM:s>(?: seconds?)?(?: and| to| until|,)? ?(?:end(?:ing)?)?(?: at)? ?<NUM:e>(?: seconds?)?', None, cmd))

# --- the property table (M3281): "set|change|modify PROPERTY of TARGET to VALUE" for every property a person can speak, and the field of CAE-UCM it becomes
SETV = '(?:' + '|'.join(G['SetVerbs']) + ')'
SETV_OPT = SETV + '? ?'
TO = '(?: (?:to|at|is|are|should be|equals?|equal to|=|as))? ?'
TO_TEXT = '(?: (?:to|as|is|are|=))? '

def tmpl(node, repl):
    """the command of a row with its placeholders filled: {time} is a Simple Time of a and b, {flag} a truth value"""
    if isinstance(node, dict): return {k: tmpl(v, repl) for k, v in node.items()}
    if isinstance(node, list): return [tmpl(v, repl) for v in node]
    if isinstance(node, str) and node in repl: return repl[node]
    return node

def material(name, bands=None, fraction=None):
    if fraction is not None:
        return {'Header': 'OSD-MTR-V1.5', 'MaterialID': name, 'Acoustic': {'Absorption': [{'FrequencyHz': 1000, 'Fraction': fraction}]}}
    return {'Header': 'OSD-MTR-V1.5', 'MaterialID': name, 'Acoustic': {'Absorption': [{'FrequencyHz': f, 'Fraction': c} for f, c in zip(G['Materials']['Bands'], bands)]}}

def value_forms(kind, unit, row):
    """the ways a value of a kind is said: (pattern, substitutions, extra) for each"""
    u = '(?: ' + unit + ')?' if unit else ''
    if kind == 'number': return [('<NUM:v>' + u, {}, None)]
    if kind == 'degrees': return [('<NUM:a>(?: degrees?)?', {}, None)]
    if kind == 'xyz': return [(XYZ_LABELLED, {}, None), (XYZ_PLAIN, {}, None)]
    if kind == 'xy': return [(XY_LABELLED, {}, None), ('<NUM:x>,? ?(?:and )?<NUM:y>' + METRES, {}, None)]
    if kind == 'pair':
        first, second = (row['Pair'] if row.get('Pair') else (None, None))
        a = '(?:' + first + ' )?' if first else '(?:from )?'
        b = '(?:' + second + ' )?' if second else ''
        sep = '(?: and| then| to| until|,)? ?'
        return [(a + '<NUM:a>' + u + (' ?' if second else '(?: seconds?)?') + sep + b + '<NUM:b>' + u, {}, None)]
    if kind == 'text': return [('(?<text>.+)', {}, {'Raw': ['text']})]
    if kind == 'onoff': return [('(?:on|true|yes|active)', {'{flag}': True}, None), ('(?:off|false|no|inactive)', {'{flag}': False}, None)]
    raise ValueError(kind)

def property_table():
    for row in G['PropertyTable']:
        ent, words, kind = row['Entity'], alt(row['Words']), row['Value']
        suffix = '(?: ' + alt(row['Suffix']) + ')?' if row.get('Suffix') else ''
        cmd = row['Command']
        if ent == 'Profile':
            of = '(?: (?:of|for) the (?:walls?|room|scene))?'
            owner = "(?:(?:walls?|room)(?:'s)? )?"
            for pat, sub, extra in value_forms(kind, row.get('Unit'), row):
                profile = {'AcousticProfileID': G.get('ProfileId', 'walls')}
                profile.update(tmpl(cmd, sub))
                emit({'Pattern': SETV_OPT + '(?:the )?' + owner + words + suffix + of + TO + pat,
                      'Command': {'UserCommandRQ': {'ModifiedScene': {'AcousticProfile': profile}}}})
        elif ent == 'Object':
            for pat, sub, extra in value_forms(kind, row.get('Unit'), row):
                to = TO_TEXT if kind == 'text' else TO
                for lead in (SETV + ' (?:the )?' + words + ' (?:of|for) (?:the )?(?<name>.+?)' + to,
                             SETV_OPT + "(?:the )?(?<name>.+?)'s " + words + to):
                    r = {'Pattern': lead + pat, 'Names': {'name': row['Target']}}
                    if extra: r.update(extra)
                    if cmd == 'moved.position': c = moved(position=['{x}', '{y}', '{z}'])
                    elif cmd == 'moved.orientation': c = moved(orientation='{a}')
                    elif cmd == 'moved.time': c = moved(time=seconds('{a}', '{b}'))
                    else: c = {'UserCommandRQ': tmpl(cmd, dict(sub, **{'{time}': seconds('{a}', '{b}')}))}
                    r['Command'] = c
                    emit(r)
        elif ent == 'Listener':
            who = "(?:my|the listener's|the listener|listener)"
            for pat, sub, extra in value_forms(kind, row.get('Unit'), row):
                lead = SETV + ' ' + who + ' ' + words + TO
                if cmd == 'listener.position':
                    emit({'Pattern': lead + pat, 'Let': {'lx': 'x', 'ly': 'y'}, 'Keep': ['lx', 'ly'], 'Command': listener_command()})
                else:
                    emit({'Pattern': lead + pat, 'Let': {'lg': 'a'}, 'Keep': ['lg'], 'Command': listener_command()})
        elif ent == 'Scene':
            emit({'Pattern': SETV + " (?:the )?(?:scene's name|name of the scene|name of this scene)" + TO_TEXT + '(?<newname>.+)', 'Raw': ['newname'], 'Sets': {'scene': 'newname'}, 'Command': {'UserCommandRQ': {'NamedScene': '{newname}'}}})
        elif ent == 'Room':
            R = alt(G['Room']['Words']); H = str(G['Room']['Height'])
            box = {'x0': '-a/2', 'x1': 'a/2', 'y0': '-b/2', 'y1': 'b/2'}
            if cmd == 'room.size':
                lead = SETV + ' (?:the )?(?:' + R + "(?:'s)? )?" + words + '(?: of the ' + R + ')?' + TO
                three = '<NUM:a>' + METRES + ' ?(?:by|x|times) ?<NUM:b>' + METRES
                emit({'Pattern': lead + three + ' ?(?:by|x|times) ?<NUM:h>' + METRES, 'Let': dict(box), 'Keep': ['a', 'b', 'h'], 'Command': closed_space('{x0}', '{x1}', '{y0}', '{y1}', '{h}')})
                emit({'Pattern': lead + three, 'Let': dict(box, h=H), 'Keep': ['a', 'b'], 'Command': closed_space('{x0}', '{x1}', '{y0}', '{y1}', '{h}')})
            elif cmd == 'room.height':
                emit({'Pattern': SETV + ' (?:the )?(?:' + R + "(?:'s)? )?(?:ceiling )?" + words + TO + '<NUM:h>' + METRES, 'Let': dict(box), 'Keep': ['h'], 'Command': closed_space('{x0}', '{x1}', '{y0}', '{y1}', '{h}')})
    # the surfaces of the room: a face or the walls, a material by name or an absorption (the Walls of the Acoustic Profile: the walls named are replaced, the others keep theirs)
    mats = {k: v for k, v in G['Materials'].items() if k not in ('_', 'Bands')}
    def walls(names, mat):
        return {'UserCommandRQ': {'ModifiedScene': {'AcousticProfile': {'AcousticProfileID': G.get('ProfileId', 'walls'),
                'Walls': [{'RightParallelepipedID': 'room', 'Face': f, 'MaterialIDOrMaterial': [mat]} for f in names]}}}}
    groups = dict((k, [v]) for k, v in G['Faces'].items())
    groups['walls'] = ['Left', 'Right', 'Front', 'Back']
    for gname, names in groups.items():
        g = '(?:the )?' + re.escape(gname) + "(?:'s)?"
        for m, bands in mats.items():
            emit({'Pattern': SETV_OPT + g + '(?: material| surface)?' + TO + '(?:made of |of |in )?(?:the )?' + m + '(?: surface| material)?', 'Command': walls(names, material(m, bands))})
            emit({'Pattern': '(?:cover|line) ' + g + ' (?:with|in) (?:the )?' + m, 'Command': walls(names, material(m, bands))})
        emit({'Pattern': SETV_OPT + g + '(?: surface)? absorption' + TO + '<NUM:v>', 'Command': walls(names, material(gname + ' absorbing', fraction='{v}'))})


def translate():
    """a Speech Object of the scene said in another language (TranslatedObjects: the Qualifier states the target language)."""
    verbs = V['Translate']
    for lang, code in G['Languages'].items():
        q = {'Header': 'TFA-SPQ-V1.5', 'SpeechQualifierID': 'translated', 'SubTypes': {}, 'Formats': {},
             'Attributes': {'Metadata': {'Language': {'LanguageFormat': 'ISO 639-1', 'LanguageCode': code}}}}
        cmd = {'UserCommandRQ': {'TranslatedObjects': {'Objects': [{'ObjectID': '{name}', 'SpeechQualifier': q}]}}}
        into = '(?: (?:in|into|to))? ' + lang
        emit(rule(MEMBER, '(?:' + verbs + ') (?:the )?(?<name>.+?)' + into, None, cmd))
        emit(rule(MEMBER, '(?:' + verbs + ') ' + lang + ' (?:the )?(?<name>.+?)', None, cmd))
        emit(rule(NEWEST, '(?:' + verbs + ') (?:' + PRON + ')' + into, None, cmd))

def profiles():
    """acoustic profiles by name: one kept is applied to the scene, the acoustics of the scene are kept under a name, a kept one is deleted (M3282 UCM-4)."""
    P = G['Profiles']
    apply, save, delete = alt(P['Apply']), alt(P['Save']), alt(P['Delete'])
    scene = '(?: to the (?:scene|room))?'
    apply_cmd = {'UserCommandRQ': {'ModifiedScene': {'AcousticProfile': '{pname}'}}}
    emit({'Pattern': '(?:' + apply + ') (?:the )?(?<pname>.+?) (?:acoustic profile|acoustics|profile)' + scene, 'Raw': ['pname'], 'Command': apply_cmd})
    emit({'Pattern': '(?:' + apply + ') (?:the )?(?:acoustic profile|profile) (?<pname>.+?)' + scene, 'Raw': ['pname'], 'Command': apply_cmd})
    emit({'Pattern': '(?:' + apply + ') (?<pname>.+?) to the (?:scene|room)', 'Raw': ['pname'], 'Command': apply_cmd})
    emit({'Pattern': '(?:' + save + ') (?:the |these |this )?(?:room )?(?:acoustics|acoustic profile|acoustic properties)(?: as| called| named| under the name)? (?<pname>.+)', 'Raw': ['pname'],
          'Command': {'UserCommandRQ': {'ModifiedScene': {'AcousticProfile': {'AcousticProfileID': '{pname}'}}}}})
    emit({'Pattern': '(?:' + delete + ') (?:the )?(?:acoustic profile|profile|acoustics) (?<pname>.+)', 'Raw': ['pname'],
          'Command': {'UserCommandRQ': {'DeletedObjects': {'Objects': [{'AcousticProfileID': '{pname}'}]}}}})

# ---------------------------------------------------------------------------------------------------------------- the order: the more specific a sentence, the earlier
def compile_rules():
    listener()
    rooms()
    centre()
    scenes()
    profiles()
    times()
    by_direction_named = [MEMBER]
    # named, by direction and amount; coordinates; turn; level; remove; delete; undo; play
    by_direction()
    coordinates()
    turn()
    level()
    remove_delete()
    rename_object()
    rendering()
    undo_play()
    raise_lower()
    approach()
    beside_me()
    by_direction_bare()
    rooms_loose()
    property_table()
    translate()
    return RULES

def synonyms(phrases):
    """The phrases of a listing, and each with a word of PhraseSynonyms said another way ('show' as 'display'): the same listing, the same command."""
    out = []
    for p in phrases:
        found = [re.sub(r'\b%s\b' % re.escape(w), a, p) for w, alts in G.get('PhraseSynonyms', {}).items() if re.search(r'\b%s\b' % re.escape(w), p) for a in alts]
        for q in [p] + found:
            if q not in out:
                out.append(q)
    return out

def build():
    context = json.loads(json.dumps(BASE))
    context['Rules'] = compile_rules()
    context['Shortcuts'] = [{'Phrases': synonyms(l['Phrases']), 'Command': l['Command']} for l in G['Listings']]
    # WHICH MATERIALS ARE AVAILABLE: asked, answered from the same list the rules are made from (what is not in it is not understood)
    names = [k for k in G['Materials'] if k not in ('_', 'Bands')]
    say = 'I know these materials: ' + ', '.join(names[:-1]) + ' and ' + names[-1] + '. Say for example: set the left wall to carpet, or cover the ceiling with foam.'
    context.setdefault('Asks', []).insert(0, {'Words': ['which materials', 'what materials', 'list the materials', 'list materials', 'materials available', 'available materials', 'materials do you', 'materials can i', 'materials are there', 'show the materials', 'show materials'], 'Say': say})
    text = json.dumps(context, indent=2, ensure_ascii=False) + '\n'
    return text

if __name__ == '__main__':
    text = build()
    if '--check' in sys.argv:
        current = open(OUT, encoding='utf-8', newline='').read().replace('\r\n', '\n')
        print('in step' if current == text else 'NOT in step: run compile.py')
        sys.exit(0 if current == text else 1)
    target = sys.argv[sys.argv.index('--out') + 1] if '--out' in sys.argv else OUT
    open(target, 'w', encoding='utf-8', newline='').write(text.replace('\n', '\r\n') if '--lf' not in sys.argv else text)
    print('%d rules -> %s' % (len(RULES), target))
