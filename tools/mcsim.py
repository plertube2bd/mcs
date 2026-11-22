#!/usr/bin/env python3
"""mcsim — run a compiled MC# datapack without Minecraft.

Simulates the parts of the game that MC# code mostly talks to:
  scoreboard (32-bit ints, floor division), data storage + function macros, function / schedule / #tick,
  execute (if/unless score|entity|data|loaded, as, store …), tellraw / say, and a small entity model
  (summon / tag / kill / selectors with type= tag= name= scores= limit=), and scripted players.
There are no blocks or positions.

Players only exist when a script adds them. An action runs at the start of its tick (tick 0 = before load):
  join NAME · leave NAME · die NAME · respawn NAME · kill NAME (NAME killed something)
  use NAME ITEM (used / right-clicked an item) · stat NAME CRITERION [N] · trigger NAME OBJECTIVE [set|add N]
  click NAME (right-click with the held item) · hold NAME SLOT (select an inventory slot) · offhand NAME SLOT (swap into the off hand)
Players have an inventory: /give fills it (NBT and item-component syntax), and `if items` / `nbt={SelectedItem:…}` read it.

Everything it does not simulate (blocks, effects, bossbars, …) is printed as "> <command>", so the
output is a readable trace of what the datapack would do. Errors Minecraft would report print as "! …".

  python3 tools/mcsim.py out/hello                  # newest version layer, run until nothing is scheduled
  python3 tools/mcsim.py out/hello --format 15      # as Minecraft 1.20.1 (pack format 15)
  python3 tools/mcsim.py out/hello --ticks 100      # simulate 100 ticks (#tick functions run every tick)
  python3 tools/mcsim.py out/hello --do "0 join Steve" --do "5 die Steve" --do "8 respawn Steve"
"""
import argparse
import json
import os
import re
import sys

MAX_CHAIN = 65536          # gamerule maxCommandChainLength
DEFAULT_TICK_RUN = 20      # ticks to simulate when the pack has #tick functions and --ticks is not given
MAX_IDLE_TICKS = 100000


def wrap(x):
    """Java int arithmetic."""
    x &= 0xFFFFFFFF
    return x - 0x100000000 if x >= 0x80000000 else x


class CommandError(Exception):
    pass


class ChainLimit(Exception):
    pass


class Return(Exception):
    def __init__(self, value):
        self.value = value


# ---------------------------------------------------------------------------------------------- SNBT
class Snbt:
    """Minimal SNBT / JSON reader. Numbers keep their type suffix in a (value, type) tuple only where it matters."""

    def __init__(self, s):
        self.s, self.i = s, 0

    @staticmethod
    def parse(s):
        p = Snbt(s)
        v = p.value()
        p.ws()
        return v

    def ws(self):
        while self.i < len(self.s) and self.s[self.i].isspace():
            self.i += 1

    def value(self):
        self.ws()
        c = self.s[self.i]
        if c == '{':
            self.i += 1
            d = {}
            self.ws()
            if self.s[self.i] == '}':
                self.i += 1
                return d
            while True:
                self.ws()
                k = self.string() if self.s[self.i] in '"\'' else self.word()
                self.ws()
                assert self.s[self.i] == ':', f"expected ':' in {self.s!r}"
                self.i += 1
                d[k] = self.value()
                self.ws()
                if self.s[self.i] == ',':
                    self.i += 1
                    continue
                assert self.s[self.i] == '}'
                self.i += 1
                return d
        if c == '[':
            self.i += 1
            self.ws()
            if re.match(r'[BIL];', self.s[self.i:self.i + 2]):
                self.i += 2
            lst = []
            self.ws()
            if self.s[self.i] == ']':
                self.i += 1
                return lst
            while True:
                lst.append(self.value())
                self.ws()
                if self.s[self.i] == ',':
                    self.i += 1
                    continue
                assert self.s[self.i] == ']'
                self.i += 1
                return lst
        if c in '"\'':
            return self.string()
        w = self.word()
        m = re.fullmatch(r'([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)([bBsSlLfFdD]?)', w)
        if m:
            num, suf = m.group(1), m.group(2).lower()
            if suf in ('f', 'd') or ('.' in num or 'e' in num.lower()) and suf == '':
                return float(num)
            return int(float(num))
        if w == 'true':
            return 1
        if w == 'false':
            return 0
        return w

    def word(self):
        j = self.i
        while self.i < len(self.s) and (self.s[self.i].isalnum() or self.s[self.i] in '_-.+'):
            self.i += 1
        return self.s[j:self.i]

    def string(self):
        q = self.s[self.i]
        self.i += 1
        out = []
        while self.s[self.i] != q:
            if self.s[self.i] == '\\':
                self.i += 1
                esc = self.s[self.i]
                out.append({'n': '\n', 't': '\t'}.get(esc, esc))
            else:
                out.append(self.s[self.i])
            self.i += 1
        self.i += 1
        return ''.join(out)


def nbt_text(v):
    """How an NBT value shows in a text component."""
    if isinstance(v, str):
        return v
    if isinstance(v, float):
        return java_double(v) + 'd'
    if isinstance(v, list):
        return '[' + ','.join(nbt_text(x) for x in v) + ']'
    if isinstance(v, dict):
        return '{' + ','.join(f'{k}:{nbt_text(x)}' for k, x in v.items()) + '}'
    return str(v)


def java_double(v):
    if v == int(v) and abs(v) < 1e7:
        return f'{int(v)}.0'
    r = repr(v)
    return r.replace('e-0', 'E-').replace('e+', 'E').replace('e-', 'E-')


def macro_value(v):
    if isinstance(v, float):
        return java_double(v)
    if isinstance(v, (dict, list)):
        return nbt_text(v)
    return str(v)


# ---------------------------------------------------------------------------------------------- NBT paths
def path_tokens(path):
    toks = []
    for m in re.finditer(r'([^.\[\]]+)|\[(-?\d+)\]', path):
        toks.append(m.group(1) if m.group(1) is not None else int(m.group(2)))
    return toks


def nbt_get(root, path):
    cur = root
    for t in path_tokens(path):
        if isinstance(t, int):
            if not isinstance(cur, list) or not -len(cur) <= t < len(cur):
                return None
            cur = cur[t]
        else:
            if not isinstance(cur, dict) or t not in cur:
                return None
            cur = cur[t]
    return cur


def nbt_set(root, path, value):
    toks = path_tokens(path)
    cur = root
    for t, nxt in zip(toks, toks[1:]):
        if isinstance(t, int):
            cur = cur[t]
        else:
            if t not in cur or not isinstance(cur[t], (dict, list)):
                cur[t] = [] if isinstance(nxt, int) else {}
            cur = cur[t]
    last = toks[-1]
    if isinstance(last, int):
        if not isinstance(cur, list) or not -len(cur) <= last < len(cur):
            raise CommandError(f'no element {path}')
        cur[last] = value
    else:
        cur[last] = value


def nbt_remove(root, path):
    toks = path_tokens(path)
    parent = nbt_get(root, '.'.join(str(t) for t in toks[:-1])) if len(toks) > 1 else root
    if isinstance(parent, dict) and toks[-1] in parent:
        del parent[toks[-1]]
        return 1
    return 0


# ---------------------------------------------------------------------------------------------- pack layout
def in_range(fmt, spec):
    if isinstance(spec, int):
        return fmt == spec
    if isinstance(spec, list):
        return spec[0] <= fmt <= spec[-1]
    if isinstance(spec, dict):
        return spec.get('min_inclusive', 0) <= fmt <= spec.get('max_inclusive', 1 << 30)
    return False


class Pack:
    def __init__(self, root, fmt=None):
        meta = json.load(open(os.path.join(root, 'pack.mcmeta')))
        self.base_format = meta['pack'].get('pack_format') or meta['pack'].get('min_format')
        entries = meta.get('overlays', {}).get('entries', [])
        self.formats = [self.base_format] + [self.min_of(e) for e in entries]
        self.format = fmt if fmt is not None else max(self.formats)
        active = [e['directory'] for e in entries if in_range(self.format, e.get('formats', [self.min_of(e), e.get('max_format', 1 << 30)]))]
        self.dirs = [os.path.join(root, d) for d in reversed(active)] + [root]   # later overlays win
        self.fdir = 'function' if self.format >= 45 else 'functions'
        self.cache = {}

    @staticmethod
    def min_of(e):
        f = e.get('formats')
        if isinstance(f, dict):
            return f.get('min_inclusive')
        if isinstance(f, list):
            return f[0]
        if isinstance(f, int):
            return f
        return e.get('min_format')

    def _find(self, rel):
        for d in self.dirs:
            p = os.path.join(d, rel)
            if os.path.exists(p):
                return p
        return None

    def function(self, rl):
        if rl not in self.cache:
            ns, path = rl.split(':', 1)
            p = self._find(os.path.join('data', ns, self.fdir, path + '.mcfunction'))
            if p is None:
                raise CommandError(f'Unknown function {rl}')
            lines = [l.strip() for l in open(p, encoding='utf-8').read().split('\n')]
            self.cache[rl] = [l for l in lines if l and not l.startswith('#')]
        return self.cache[rl]

    def tag(self, rl, seen=None):
        ns, path = rl.split(':', 1)
        tagdir = 'function' if self.format >= 45 else 'functions'
        p = self._find(os.path.join('data', ns, 'tags', tagdir, path + '.json'))
        if p is None:
            return []
        out = []
        for v in json.load(open(p)).get('values', []):
            v = v if isinstance(v, str) else v['id']
            out.extend(self.tag(v[1:]) if v.startswith('#') else [v])
        return out


# ---------------------------------------------------------------------------------------------- simulator
class Entity:
    counter = 0

    def __init__(self, etype, tags, name=None):
        Entity.counter += 1
        self.id, self.type, self.tags = Entity.counter, etype, set(tags)
        self.name = name             # players only
        self.alive = True
        self.inv = {}                # slot → {"id", "count", "custom"}; -106 = off hand
        self.selected = 0

    @property
    def is_player(self):
        return self.type == 'minecraft:player'

    def slot_item(self, slot):
        return self.inv.get({'weapon.mainhand': self.selected, 'weapon': self.selected, 'weapon.offhand': -106}.get(slot, -1))

    def nbt(self):
        def item(it, slot=None):
            d = {'id': it['id'], 'Count': it['count'], 'tag': it['custom'], 'components': {'minecraft:custom_data': it['custom']}}
            if slot is not None:
                d['Slot'] = slot
            return d
        out = {'Tags': list(self.tags), 'Inventory': [item(it, s) for s, it in sorted(self.inv.items())]}
        if self.selected in self.inv:
            out['SelectedItem'] = item(self.inv[self.selected])
        return out

    @property
    def holder(self):
        return self.name if self.is_player else f'#e{self.id}'


class Sim:
    def __init__(self, pack, out=print):
        self.pack = pack
        self.out = out
        self.objectives = {}           # name → criteria
        self.scores = {}               # (holder, objective) → int
        self.storage = {}              # rl → compound
        self.bossbars = {}             # id → {value, max}
        self.entities = []
        self.schedule = []             # (due tick, seq, function)
        self.enabled = set()           # (player, trigger objective) that may /trigger
        self.offline = {}              # name → player entity that left
        self.seq = 0
        self.tick = 0
        self.chain = 0

    # ---- output
    def emit(self, text):
        self.out(f'[t{self.tick}] {text}')

    def log(self, cmd):
        self.emit('> ' + cmd)

    def error(self, msg):
        self.emit('! ' + msg)

    # ---- running
    def run_function(self, rl, ctx, args=None):
        try:
            lines = self.pack.function(rl)
        except CommandError as e:
            self.error(str(e))
            return 0
        result = 0
        try:
            for line in lines:
                if line.startswith('$'):
                    if args is None:
                        self.error(f'{rl}: macro line without arguments')
                        return 0
                    line = re.sub(r'\$\((\w+)\)', lambda m: macro_value(args.get(m.group(1), '')), line[1:])
                result = self.run(line, ctx)
        except Return as r:
            return r.value
        return result

    def run(self, cmd, ctx):
        """Runs one command; returns its result (0 = failed)."""
        self.chain += 1
        if self.chain > MAX_CHAIN:
            raise ChainLimit()
        try:
            return self.dispatch(cmd, ctx)
        except CommandError as e:
            self.error(f'{e}  ← {cmd}')
            return 0

    def dispatch(self, cmd, ctx):
        t = cmd.split(' ')
        head = t[0]
        if head == 'scoreboard':
            return self.scoreboard(t, cmd, ctx)
        if head == 'execute':
            return self.execute(t[1:], cmd, ctx)
        if head == 'function':
            if len(t) >= 5 and t[2] == 'with' and t[3] == 'storage':
                args = nbt_get(self.storage.get(t[4], {}), t[5]) if len(t) > 5 else self.storage.get(t[4], {})
                if not isinstance(args, dict):
                    raise CommandError(f'no macro arguments at {t[4]} {t[5] if len(t) > 5 else ""}')
                return self.run_function(t[1], ctx, args)
            if t[1].startswith('#'):
                for f in self.pack.tag(t[1][1:]):
                    self.run_function(f, ctx)
                return 1
            return self.run_function(t[1], ctx)
        if head == 'return':
            if len(t) > 1 and t[1] == 'run':
                raise Return(self.run(' '.join(t[2:]), ctx))
            raise Return(int(t[1]) if len(t) > 1 else 0)
        if head == 'schedule':
            return self.schedule_cmd(t)
        if head == 'data':
            return self.data(t, cmd)
        if head == 'tellraw':
            sel, rest = split_selector(cmd[len('tellraw '):])
            text = self.render(rest.strip(), ctx)
            if sel == '@a':
                self.emit(text)                     # broadcast (printed even with no players online)
            else:
                to = [e.name for e in self.select(sel, ctx) if e.is_player]
                if to:
                    self.emit(f'to {",".join(to)}: {text}')
                return len(to)
            return 1
        if head == 'say':
            who = (ctx.name or ctx.type) if ctx else 'Server'
            self.emit(f'[{who}] {cmd[4:]}')
            return 1
        if head == 'summon':
            return self.summon(cmd, ctx)
        if head == 'tag':
            sel, rest = split_selector(cmd[4:])
            r = rest.split()
            ents = self.select(sel, ctx)
            for e in ents:
                if r[0] == 'add':
                    e.tags.add(r[1])
                elif r[0] == 'remove':
                    e.tags.discard(r[1])
            return len(ents)
        if head == 'kill':
            ents = self.select(cmd[5:].strip() or '@s', ctx)
            self.log(cmd)
            for e in ents:
                self.entities.remove(e)
            return len(ents)
        if head == 'bossbar':
            return self.bossbar(t, cmd)
        if head == 'give':
            sel, rest = split_selector(cmd[5:])
            stack, _, count = rest.strip().rpartition(' ') if re.search(r' \d+$', rest.strip()) else (rest.strip(), '', '1')
            item = parse_item(stack)
            item['count'] = int(count)
            players = [e for e in self.select(sel, ctx) if e.is_player]
            for pl in players:
                slot = next(i for i in range(36) if i not in pl.inv)
                pl.inv[slot] = dict(item)
            self.log(cmd)
            return len(players)
        self.log(cmd)
        return 1

    # ---- scoreboard
    def need_obj(self, obj):
        if obj not in self.objectives:
            raise CommandError(f"Unknown scoreboard objective '{obj}'")

    def holders(self, h, ctx):
        if h.startswith('@'):
            return [e.holder for e in self.select(h, ctx)]
        return [h]

    def get(self, h, obj, ctx):
        self.need_obj(obj)
        hs = self.holders(h, ctx)
        if len(hs) != 1 or (hs[0], obj) not in self.scores:
            raise CommandError(f'No score for {h} in {obj}')
        return self.scores[(hs[0], obj)]

    def scoreboard(self, t, cmd, ctx):
        if t[1] == 'objectives':
            if t[2] == 'add':
                if t[3] in self.objectives:
                    raise CommandError(f'An objective already exists by the name {t[3]}')
                self.objectives[t[3]] = t[4]
                return 1
            if t[2] == 'remove':
                self.need_obj(t[3])
                del self.objectives[t[3]]
                self.scores = {k: v for k, v in self.scores.items() if k[1] != t[3]}
                return 1
            self.log(cmd)
            return 1
        sub = t[2]
        if sub in ('set', 'add', 'remove'):
            obj = t[4]
            self.need_obj(obj)
            n = int(t[5])
            for h in self.holders(t[3], ctx):
                old = self.scores.get((h, obj), 0)
                self.scores[(h, obj)] = n if sub == 'set' else wrap(old + n if sub == 'add' else old - n)
            return 1
        if sub == 'get':
            return self.get(t[3], t[4], ctx)
        if sub == 'reset':
            if t[3] == '*':
                self.scores = {k: v for k, v in self.scores.items() if len(t) > 4 and k[1] != t[4]}
            else:
                for h in self.holders(t[3], ctx):
                    self.scores = {k: v for k, v in self.scores.items() if not (k[0] == h and (len(t) <= 4 or k[1] == t[4]))}
            return 1
        if sub == 'operation':
            tobj, op, sh, sobj = t[4], t[5], t[6], t[7]
            self.need_obj(tobj)
            src = self.get(sh, sobj, ctx)
            for h in self.holders(t[3], ctx):
                a = self.scores.get((h, tobj), 0)
                if op == '=':
                    r = src
                elif op == '+=':
                    r = wrap(a + src)
                elif op == '-=':
                    r = wrap(a - src)
                elif op == '*=':
                    r = wrap(a * src)
                elif op in ('/=', '%='):
                    if src == 0:
                        raise CommandError('Cannot divide by zero')
                    r = wrap(a // src) if op == '/=' else a % src      # Python floors, like Mth.floorDiv / floorMod
                elif op == '<':
                    r = min(a, src)
                elif op == '>':
                    r = max(a, src)
                elif op == '><':
                    r = src
                    self.scores[(sh, sobj)] = a
                else:
                    raise CommandError(f'bad operation {op}')
                self.scores[(h, tobj)] = r
            return 1
        if sub == 'enable':
            self.need_obj(t[4])
            for h in self.holders(t[3], ctx):
                self.enabled.add((h, t[4]))
            return 1
        if sub == 'display':
            return 1
        self.log(cmd)
        return 1

    # ---- schedule
    def schedule_cmd(self, t):
        if t[1] == 'clear':
            n = len(self.schedule)
            self.schedule = [s for s in self.schedule if s[2] != t[2]]
            return n - len(self.schedule)
        rl, time = t[2], t[3]
        mode = t[4] if len(t) > 4 else 'replace'
        n = int(re.match(r'\d+', time).group())
        n *= {'t': 1, 's': 20, 'd': 24000}.get(time[-1], 1)
        if mode == 'replace':
            self.schedule = [s for s in self.schedule if s[2] != rl]
        self.seq += 1
        self.schedule.append((self.tick + n, self.seq, rl))
        return 1

    # ---- data
    def data(self, t, cmd):
        if t[1] == 'modify' and t[2] == 'storage':
            rl, path, action = t[3], t[4], t[5]
            root = self.storage.setdefault(rl, {})
            rest = cmd.split(' ', 7)
            if action == 'set' and t[6] == 'value':
                nbt_set(root, path, Snbt.parse(rest[7]))
            elif action == 'set' and t[6] == 'from' and t[7] == 'storage':
                v = nbt_get(self.storage.get(t[8], {}), t[9])
                if v is None:
                    raise CommandError(f'Found no elements matching {t[9]}')
                nbt_set(root, path, json.loads(json.dumps(v)))
            elif action == 'append' and t[6] == 'value':
                lst = nbt_get(root, path)
                if lst is None:
                    nbt_set(root, path, [])
                    lst = nbt_get(root, path)
                lst.append(Snbt.parse(rest[7]))
            else:
                self.log(cmd)
            return 1
        if t[1] == 'remove' and t[2] == 'storage':
            return nbt_remove(self.storage.get(t[3], {}), t[4])
        if t[1] == 'get' and t[2] == 'storage':
            v = nbt_get(self.storage.get(t[3], {}), t[4])
            if v is None:
                raise CommandError(f'Found no elements matching {t[4]}')
            scale = float(t[5]) if len(t) > 5 else 1.0
            if isinstance(v, (int, float)):
                import math
                return int(math.floor(v * scale))
            return len(v)
        self.log(cmd)
        return 1

    # ---- bossbar (values only; everything is also logged)
    def bossbar(self, t, cmd):
        if t[1] == 'get':
            b = self.bossbars.get(t[2])
            if b is None:
                raise CommandError(f'No bossbar exists with the ID {t[2]}')
            return b.get(t[3], 0 if t[3] == 'value' else 100)
        if t[1] == 'add':
            self.bossbars.setdefault(t[2], {})
        if t[1] == 'set' and t[3] in ('value', 'max'):
            self.bossbars.setdefault(t[2], {})[t[3]] = int(t[4])
        if t[1] == 'remove':
            self.bossbars.pop(t[2], None)
        self.log(cmd)
        return 1

    # ---- entities
    def summon(self, cmd, ctx):
        m = re.match(r'summon (\S+)(?: \S+ \S+ \S+)?(?: (.*))?$', cmd)
        etype = m.group(1)
        tags = []
        if m.group(2):
            nbt = Snbt.parse(m.group(2))
            tags = nbt.get('Tags', []) if isinstance(nbt, dict) else []
        self.entities.append(Entity(etype if ':' in etype else 'minecraft:' + etype, tags))
        self.log(cmd)
        return 1

    def select(self, sel, ctx):
        m = re.match(r'@([aeprsn])(?:\[(.*)\])?$', sel)
        if not m:
            return [e for e in self.entities if e.is_player and e.name == sel]   # a player name
        kind, args = m.group(1), m.group(2) or ''
        if kind == 's':
            pool = [ctx] if ctx is not None else []
        elif kind in 'en':
            pool = [e for e in self.entities if e.alive]          # @e only selects living entities
        elif kind == 'a':
            pool = [e for e in self.entities if e.is_player]      # @a includes dead players
        else:
            pool = [e for e in self.entities if e.is_player and e.alive][:1]
        limit = None
        for a in split_args(args):
            k, _, v = a.partition('=')
            neg = v.startswith('!')
            v = v[1:] if neg else v
            if k == 'type':
                v = v if ':' in v else 'minecraft:' + v
                pool = [e for e in pool if (e.type == v) != neg]
            elif k == 'tag':
                pool = [e for e in pool if ((v in e.tags) if v else bool(e.tags)) != neg]
            elif k == 'name':
                pool = [e for e in pool if (e.name == v) != neg]
            elif k == 'scores':
                for cond in split_args(v.strip('{}')):
                    obj, _, rng = cond.partition('=')
                    pool = [e for e in pool if self.score_matches(e.holder, obj, rng, e)]
            elif k == 'nbt':
                pat = Snbt.parse(v)
                pool = [e for e in pool if nbt_matches(pat, e.nbt()) != neg]
            elif k == 'limit':
                limit = int(v)
        return pool[:limit] if limit is not None else pool

    # ---- execute
    def execute(self, t, cmd, ctx):
        ctxs = [ctx]
        stores = []
        i = 0
        while i < len(t):
            w = t[i]
            if w in ('if', 'unless'):
                want = w == 'if'
                kind = t[i + 1]
                if kind == 'score':
                    h, obj = t[i + 2], t[i + 3]
                    if t[i + 4] == 'matches':
                        test = lambda c, h=h, obj=obj, r=t[i + 5]: self.score_matches(h, obj, r, c)
                        i += 6
                    else:
                        test = lambda c, h=h, obj=obj, op=t[i + 4], h2=t[i + 5], o2=t[i + 6]: self.score_compare(h, obj, op, h2, o2, c)
                        i += 7
                elif kind == 'entity':
                    sel = t[i + 2]
                    if i + 3 >= len(t):                 # last subcommand: the result is the number of matches
                        n = sum(len(self.select(sel, c)) for c in ctxs)
                        r = n if want else (1 if n == 0 else 0)
                        return self.finish_stores(stores, r, r > 0)
                    test = lambda c, s=sel: bool(self.select(s, c))
                    i += 3
                elif kind == 'data' and t[i + 2] == 'storage':
                    test = lambda c, rl=t[i + 3], p=t[i + 4]: nbt_get(self.storage.get(rl, {}), p) is not None
                    i += 5
                elif kind == 'items' and t[i + 2] == 'entity':
                    sel, j = selector_at(t, i + 3)
                    slot, pred = t[j], t[j + 1]
                    test = lambda c, sel=sel, slot=slot, pred=pred: any(item_matches(e.slot_item(slot), pred) for e in self.select(sel, c))
                    i = j + 2
                elif kind == 'loaded':
                    test = lambda c: True
                    i += 5
                else:
                    self.log(cmd)                       # blocks, items, predicates…: not simulated
                    return 0
                ctxs = [c for c in ctxs if test(c) == want]
                if i >= len(t):
                    return self.finish_stores(stores, len(ctxs), len(ctxs) > 0)
            elif w == 'as':
                sel, i = selector_at(t, i + 1)
                ctxs = [e for c in ctxs for e in self.select(sel, c)]
            elif w == 'at':
                sel, i = selector_at(t, i + 1)
                ctxs = [c for c in ctxs if self.select(sel, c)]
            elif w in ('positioned', 'rotated'):
                if t[i + 1] == 'as':
                    sel, i = selector_at(t, i + 2)
                    ctxs = [c for c in ctxs if self.select(sel, c)]
                else:
                    i += 4 if w == 'positioned' else 3
            elif w == 'facing':
                if t[i + 1] == 'entity':
                    sel, i = selector_at(t, i + 2)
                    i += 1
                else:
                    i += 4
            elif w in ('anchored', 'align', 'in', 'on'):
                i += 2
            elif w == 'store':
                mode = t[i + 1]
                if t[i + 2] == 'score':
                    stores.append((mode, 'score', t[i + 3], t[i + 4]))
                    i += 5
                elif t[i + 2] == 'storage':
                    stores.append((mode, 'storage', t[i + 3], t[i + 4], t[i + 5], float(t[i + 6])))
                    i += 7
                elif t[i + 2] == 'bossbar':
                    stores.append((mode, 'bossbar', t[i + 3], t[i + 4]))
                    i += 5
                elif t[i + 2] == 'entity':
                    stores.append((mode, 'entity', cmd))
                    _, i = selector_at(t, i + 3)
                    i += 3
                else:
                    self.log(cmd)
                    return 0
            elif w == 'run':
                inner = ' '.join(t[i + 1:])
                total, ok = 0, False
                for c in ctxs:
                    r = self.run(inner, c)
                    total, ok = r, ok or r != 0 or inner.startswith(('function', 'tellraw', 'say', 'summon', 'tag'))
                    self.finish_stores(stores, r, r != 0 or inner.startswith('summon'), c)
                return total if ctxs else 0
            else:
                self.log(cmd)
                return 0
        return len(ctxs)

    def finish_stores(self, stores, result, success, ctx=None):
        for s in stores:
            v = result if s[0] == 'result' else (1 if success else 0)
            if s[1] == 'score':
                self.need_obj(s[3])
                for h in self.holders(s[2], ctx):
                    self.scores[(h, s[3])] = wrap(int(v))
            elif s[1] == 'storage':
                typ, scale = s[4], s[5]
                val = v * scale
                val = float(val) if typ in ('float', 'double') else int(val)
                nbt_set(self.storage.setdefault(s[2], {}), s[3], val)
            elif s[1] == 'bossbar':
                self.bossbars.setdefault(s[2], {})[s[3]] = int(v)
                self.log(f'bossbar set {s[2]} {s[3]} {int(v)}  (stored)')
            elif s[1] == 'entity':
                self.log(s[2])
        return result

    def score_matches(self, h, obj, r, ctx):
        hs = self.holders(h, ctx)
        if len(hs) != 1 or (hs[0], obj) not in self.scores:
            return False
        v = self.scores[(hs[0], obj)]
        if '..' in r:
            lo, hi = r.split('..')
            return (lo == '' or v >= int(lo)) and (hi == '' or v <= int(hi))
        return v == int(r)

    def score_compare(self, h, obj, op, h2, o2, ctx):
        a, b = self.holders(h, ctx), self.holders(h2, ctx)
        if len(a) != 1 or len(b) != 1 or (a[0], obj) not in self.scores or (b[0], o2) not in self.scores:
            return False
        x, y = self.scores[(a[0], obj)], self.scores[(b[0], o2)]
        return {'<': x < y, '<=': x <= y, '=': x == y, '>': x > y, '>=': x >= y}[op]

    # ---- text
    def render(self, raw, ctx):
        try:
            comp = Snbt.parse(raw)
        except Exception:
            return raw
        return self.comp_text(comp, ctx)

    def comp_text(self, c, ctx):
        if isinstance(c, str):
            return c
        if isinstance(c, list):
            return ''.join(self.comp_text(x, ctx) for x in c)
        if not isinstance(c, dict):
            return str(c)
        s = ''
        if 'text' in c:
            s = str(c['text'])
        elif 'score' in c:
            sc = c['score']
            try:
                s = str(self.get(sc['name'], sc['objective'], ctx))
            except CommandError:
                s = ''
        elif 'nbt' in c and 'storage' in c:
            v = nbt_get(self.storage.get(c['storage'], {}), c['nbt'])
            s = '' if v is None else (c.get('separator', ', ').join(nbt_text(x) for x in v) if isinstance(v, list) and c['nbt'].endswith('[]') else nbt_text(v))
        elif 'selector' in c:
            ents = self.select(c['selector'], ctx)
            s = ', '.join(e.type.split(':')[1] for e in ents)
        elif 'nbt' in c:
            s = f'<nbt {c["nbt"]}>'
        elif 'translate' in c:
            s = c['translate']
        return s + ''.join(self.comp_text(x, ctx) for x in c.get('extra', []))

    # ---- driver
    def load(self):
        self.chain = 0
        try:
            for f in self.pack.tag('minecraft:load'):
                self.run_function(f, None)
        except ChainLimit:
            self.error('maxCommandChainLength exceeded (infinite loop?)')

    # ---- scripted players
    def stat(self, player, criterion, n=1):
        for obj, crit in self.objectives.items():
            if crit == criterion:
                key = (player.name, obj)
                self.scores[key] = wrap(self.scores.get(key, 0) + n)

    def act(self, action):
        w = action.split()
        verb, name = w[0], w[1]
        p = next((e for e in self.entities if e.is_player and e.name == name), None)
        self.emit(f'* {action}')
        if verb == 'join':
            if p is not None:
                return
            p = self.offline.pop(name, None) or Entity('minecraft:player', [], name)
            self.entities.append(p)
            return
        if p is None:
            self.error(f'{name} is not online')
            return
        if verb == 'leave':
            self.entities.remove(p)
            self.offline[name] = p
            self.stat(p, 'minecraft.custom:minecraft.leave_game')
        elif verb == 'die':
            p.alive = False
            self.stat(p, 'deathCount')
            self.stat(p, 'minecraft.custom:minecraft.deaths')
        elif verb == 'respawn':
            p.alive = True
        elif verb == 'kill':
            self.stat(p, 'totalKillCount')
        elif verb == 'use':
            item = w[2] if ':' in w[2] else 'minecraft:' + w[2]
            self.stat(p, f'minecraft.used:{item}')
        elif verb == 'click':
            it = p.inv.get(p.selected) or p.inv.get(-106)
            if it:
                self.stat(p, f'minecraft.used:{it["id"]}')
        elif verb == 'hold':
            p.selected = int(w[2])
        elif verb == 'offhand':
            k = int(w[2])
            p.inv[k], p.inv[-106] = p.inv.get(-106), p.inv.get(k)
            p.inv = {s: v for s, v in p.inv.items() if v is not None}
        elif verb == 'stat':
            self.stat(p, w[2], int(w[3]) if len(w) > 3 else 1)
        elif verb == 'trigger':
            obj = w[2]
            if self.objectives.get(obj) != 'trigger' or (name, obj) not in self.enabled:
                self.error(f'{name} cannot trigger {obj} (not a trigger objective, or not enabled)')
                return
            self.enabled.discard((name, obj))
            mode, n = (w[3], int(w[4])) if len(w) > 4 else ('add', 1)
            old = self.scores.get((name, obj), 0)
            self.scores[(name, obj)] = n if mode == 'set' else wrap(old + n)
        else:
            self.error(f'unknown action: {action}')

    def run_actions(self):
        for a in [a for t, a in self.actions if t == self.tick]:
            self.act(a)

    def step(self):
        """One game tick: scripted actions, #tick functions, then scheduled functions that are due."""
        self.tick += 1
        self.chain = 0
        self.run_actions()
        try:
            for f in self.pack.tag('minecraft:tick'):
                self.run_function(f, None)
            due = sorted(s for s in self.schedule if s[0] <= self.tick)
            self.schedule = [s for s in self.schedule if s[0] > self.tick]
            for _, _, f in due:
                self.chain = 0
                self.run_function(f, None)
        except ChainLimit:
            self.error('maxCommandChainLength exceeded (infinite loop?)')

    def run_all(self, ticks=None, actions=()):
        self.actions = sorted(actions)
        last = max((t for t, _ in self.actions), default=0)
        self.run_actions()                                       # tick 0: before load
        self.load()
        if ticks is None:
            if self.pack.tag('minecraft:tick'):
                ticks = max(DEFAULT_TICK_RUN, last + 5)
            else:
                idle = 0
                while (self.schedule or any(t > self.tick for t, _ in self.actions)) and idle < MAX_IDLE_TICKS:
                    nxt = [s[0] for s in self.schedule] + [t for t, _ in self.actions if t > self.tick]
                    self.tick = min(nxt) - 1                         # jump to the next tick with something to do
                    self.step()
                    idle += 1
                return
        for _ in range(ticks):
            self.step()


def parse_item(stack):
    """'id{nbt}' (≤1.20.4) or 'id[component=value,…]' (1.20.5+) → {"id", "custom"}"""
    m = re.match(r'([a-z0-9_.:-]+)(.*)$', stack, re.S)
    iid, rest = m.group(1), m.group(2)
    iid = iid if ':' in iid else 'minecraft:' + iid
    custom = {}
    if rest.startswith('{'):
        tag = Snbt.parse(rest)
        custom = tag if isinstance(tag, dict) else {}
    elif rest.startswith('['):
        for comp in split_args(rest[1:-1]):
            k, _, v = comp.partition('=')
            if k.strip() in ('minecraft:custom_data', 'custom_data'):
                custom = Snbt.parse(v)
    return {'id': iid, 'custom': custom}


def item_matches(item, pred):
    """`execute if items` predicate: '*' or an ID, optionally [minecraft:custom_data~{…}] / [custom_data={…}]."""
    if not item:
        return False
    m = re.match(r'(\*|[a-z0-9_.:-]+)(?:\[(.*)\])?$', pred, re.S)
    pid = m.group(1)
    if pid != '*' and item['id'] != (pid if ':' in pid else 'minecraft:' + pid):
        return False
    for test in split_args(m.group(2) or ''):
        mm = re.match(r'(?:minecraft:)?custom_data(~|=)(.*)$', test, re.S)
        if not mm:
            return False                         # other component tests: not simulated
        want = Snbt.parse(mm.group(2))
        if not (nbt_matches(want, item['custom']) if mm.group(1) == '~' else want == item['custom']):
            return False
    return True


def nbt_matches(pattern, value):
    """NBT partial match, as in nbt={…} selectors: compounds by key, lists by 'every pattern element matches some element'."""
    if isinstance(pattern, dict):
        return isinstance(value, dict) and all(k in value and nbt_matches(v, value[k]) for k, v in pattern.items())
    if isinstance(pattern, list):
        return isinstance(value, list) and all(any(nbt_matches(p, v) for v in value) for p in pattern)
    if isinstance(pattern, (int, float)) and isinstance(value, (int, float)):
        return pattern == value
    return pattern == value


def split_selector(s):
    """'@e[tag=a] rest' → ('@e[tag=a]', ' rest')"""
    s = s.lstrip()
    depth = 0
    for i, ch in enumerate(s):
        if ch == '[':
            depth += 1
        elif ch == ']':
            depth -= 1
        elif ch == ' ' and depth == 0:
            return s[:i], s[i:]
    return s, ''


def selector_at(t, i):
    """Joins tokens of a selector that contains spaces; returns (selector, next index)."""
    s = t[i]
    while s.count('[') > s.count(']'):
        i += 1
        s += ' ' + t[i]
    return s, i + 1


def split_args(args):
    out, depth, cur = [], 0, ''
    for ch in args:
        if ch in '[{':
            depth += 1
        elif ch in ']}':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


def parse_actions(specs):
    """["5 die Steve", …] → [(5, "die Steve"), …]"""
    out = []
    for spec in specs:
        t, _, action = spec.strip().partition(' ')
        out.append((int(t), action.strip()))
    return out


def simulate(pack_dir, fmt=None, ticks=None, actions=()):
    """Runs a pack and returns the output lines. actions: [(tick, "join Steve"), …]"""
    lines = []
    Entity.counter = 0
    sim = Sim(Pack(pack_dir, fmt), lines.append)
    sim.run_all(ticks, actions)
    return lines


def main():
    ap = argparse.ArgumentParser(description='Run a compiled MC# datapack without Minecraft.')
    ap.add_argument('pack', help='datapack folder (contains pack.mcmeta)')
    ap.add_argument('--format', type=int, help='pack format to simulate (default: the newest layer)')
    ap.add_argument('--ticks', type=int, help='ticks to simulate after load (default: until nothing is scheduled)')
    ap.add_argument('--formats', action='store_true', help='list the pack formats that have their own layer')
    ap.add_argument('--do', action='append', default=[], metavar='"TICK ACTION"', help='scripted player action, e.g. "0 join Steve", "5 use Steve carrot_on_a_stick"')
    a = ap.parse_args()
    if a.formats:
        print(' '.join(map(str, Pack(a.pack).formats)))
        return
    for line in simulate(a.pack, a.format, a.ticks, parse_actions(a.do)):
        print(line)


if __name__ == '__main__':
    main()
