# MC# 라이브러리 레퍼런스

MC#에는 내장 라이브러리가 다섯 개 있습니다. 모두 데이터팩 안에 함께 컴파일되므로, 출력된 팩은 다른 데이터팩이나 모드에 의존하지 않습니다.

| 라이브러리 | 불러오는 방법 | 내용 |
|---|---|---|
| **Core** | 항상 사용 가능 | `asm`, `m.Console`, `Position`, `GetWorldSpawn`, `SetWorldSpawn`, `SpawnEntity`, `m.SetBlock`/`FillBlock`/`IsBlock`, `[Load]`, `[Tick]` |
| **EntityLib** | `use EntityLib;` | `Entity`, `EntityType`, `m.SpawnEntityObject` |
| **BlockLib** | `use BlockLib;` | `Block`과 블록마다 하나씩 있는 클래스(`OakStairs`, `Stone`, …), `m.SetBlockObject`/`FillBlockObject`/`IsBlockObject` |
| **PlayerLib** | `use PlayerLib;` | `Player` (제어, 인벤토리 칸, 효과 등), `Slot`, `GameMode` |
| **TuiLib** | `use TuiLib;` | 클릭할 수 있는 채팅 `Button`, 스타일 `Text`, `Tui.Menu`/`Print`/`Title` |

### ID는 버전별로 검사됩니다

블록·아이템·엔티티·효과 ID는 모두 대상 버전별 실제 데이터와 대조해 검사합니다. 이 데이터는 1.20.1부터 26.3까지 모든 릴리스에서 Mojang 데이터 생성기로 추출했고, 실험적 기능 플래그 뒤에 있는 콘텐츠는 제외했습니다.

```
error MCS: [1.20.1, …, 1.21.2-1.21.3] The block 'pale_oak_log' does not exist in this version (exists in: 1.21.4 – 26.3). Use #if or change the target versions.
error MCS: Only vanilla (minecraft:) block IDs are allowed — the datapack must not depend on other packs or mods: 'create:cogwheel'
```

- `minecraft:` 접두사는 생략할 수 있습니다 (`"stone"` = `"minecraft:stone"`).
- **이름이 바뀐 ID는 자동으로 대응됩니다:** `grass` ↔ `short_grass`, `chain` ↔ `iron_chain`, `boat` ↔ `oak_boat`, `scute` ↔ `turtle_scute` 등

---

## Core

### `asm` — 명령어 직접 실행

```csharp
asm say hello;                         // 명령어 하나: 중괄호 없이, 끝의 ;는 생략 가능
asm {                                  // 명령어 여러 개: 한 줄에 하나씩
    effect give @a minecraft:speed 10 1
    tellraw @a {"text":"JSON 안의 중괄호도 괜찮습니다","color":"gold"}
}
```

- 맨 앞의 `/`는 자동으로 제거됩니다.
- `#`으로 시작하는 줄은 mcfunction 주석으로 남습니다.
- `asm` 안에서는 MC# 변수를 쓸 수 없습니다 (그대로 텍스트로 들어갑니다).

### `m.Console.WriteLine(value)`

모든 플레이어에게 메시지를 보여 줍니다 (`tellraw @a`).

```csharp
m.Console.WriteLine($"점수: {score}, 속도: {speed}, 생존: {alive}");
m.Console.WriteLine("총 " + count + "마리");
m.Console.WriteLine($"UUID: {string.Join("", x.uuid)}");
```

| 넣는 값의 타입 | 표시 방식 |
|---|---|
| 상수 | 컴파일할 때 계산해서 텍스트로 표시 |
| `int` | 스코어 값 |
| `float` / `double` | `-12.345` (소수점 3자리) |
| `bool` | `True` / `False` |
| `Entity`, `Player` | 이름 (셀렉터) |
| `Button`, `Text` | 클릭 가능한 버튼 / 스타일 텍스트 |

### `Position`

컴파일 타임 값입니다. 실행 위치(컨텍스트)와 좌표로 이루어집니다.

| 표현식 | 의미 |
|---|---|
| `GetWorldSpawn()` | 월드 스폰 지점 |
| `new Position(x, y, z)` | 절대 좌표 (상수) |
| `Position.Here` | 현재 실행 위치 |
| `entity.Position`, `player.Position` | 해당 엔티티/플레이어의 위치 |
| `pos.Offset(dx, dy, dz)` | `pos`에서 오프셋만큼 이동한 위치 (상수) |

```csharp
Position origin = GetWorldSpawn().Offset(3, 0, 3);
```

### `SpawnEntity(string id, Position pos, int count = 1)`

ID로 엔티티를 `count`마리 소환합니다. `count`에는 실행 중의 값도 쓸 수 있습니다.

```csharp
SpawnEntity("minecraft:zombie", GetWorldSpawn(), 10);
SpawnEntity("cow", origin.Offset(0, 1, 0), herdSize);
```

### 블록: `m.SetBlock`, `m.FillBlock`, `m.IsBlock`

| 메서드 | 명령어 | 반환값 |
|---|---|---|
| `m.SetBlock(string block, Position pos)` | `setblock` | `bool` 성공 여부 |
| `m.FillBlock(string block, Position from, Position to)` | `fill` | `bool` 성공 여부 |
| `m.IsBlock(string block, Position pos)` | `execute if block` | `bool` |

```csharp
m.SetBlock("minecraft:gold_block", origin);
m.SetBlock("oak_stairs[facing=east,half=top]", origin.Offset(0, 1, 0));   // 상태도 검사됩니다
m.FillBlock("stone", origin, origin.Offset(4, 0, 4));
if (m.IsBlock("gold_block", origin)) { ... }
```

`FillBlock`의 두 모서리는 같은 위치 기준이거나(예: `p`와 `p.Offset(...)`) 절대 좌표여야 합니다.

### `SetWorldSpawn(Position pos)`

월드 스폰을 설정하고, **`GetWorldSpawn()`이 쓰는 마커도 함께 옮깁니다.** `/reload`를 실행할 필요가 없습니다.

```csharp
SetWorldSpawn(new Position(100, 70, -20));
```

- 마커는 대상 청크가 로드된 뒤에 옮겨지므로 최소 한 틱이 걸립니다. 같은 틱의 뒤쪽 코드는 아직 이전 위치를 봅니다.
- 1.21.9 이상에서는 스폰 청크가 없어졌기 때문에 스폰 청크를 forceload합니다.

> **`/reload`를 실행하지 않는 이유:** 리로드하면 `Main`과 `[Load]`가 다시 실행됩니다. `Main`에서 `SetWorldSpawn`을 호출하면 리로드가 끝없이 반복되고, 리로드할 때마다 다른 데이터팩도 전부 다시 로드됩니다. 마커를 직접 옮기면 이런 문제 없이 같은 효과를 얻습니다. 꼭 리로드해야 한다면 `m.Reload()`를 쓰되, `Main`이나 `[Load]`에서는 절대 호출하지 마세요.

### `[Load]`, `[Tick]`

```csharp
[Load] public static void Init() { ... }    // #minecraft:load
[Tick] public static void Update() { ... }  // #minecraft:tick
```

`Main()`은 진입점이며, `[Load]` 메서드들이 실행된 다음 자동으로 실행됩니다.

---

## EntityLib

```csharp
use EntityLib;

new Entity : Zombie x;                       // = Entity x = new Entity(EntityType.Zombie);
x.maxHealth = 100f;
x.Health = 100f;
if (!m.SpawnEntityObject(x, GetWorldSpawn())) return;
```

`EntityType`에는 바닐라 엔티티 타입이 모두 들어 있습니다 (`EntityType.CaveSpider` → `minecraft:cave_spider`). 대상 버전에 없는 타입은 컴파일 오류입니다.

### `Entity` 속성

| 속성 | 타입 | 읽기 | 쓰기 | 마인크래프트 대응 |
|---|---|:-:|:-:|---|
| `Health` | `float` | ✅ | ✅ | NBT `Health` |
| `maxHealth` / `MaxHealth` | `float` | ✅ | ✅ ¹ | 속성 `max_health` |
| `MovementSpeed` | `float` | ✅ | ✅ ¹ | 속성 `movement_speed` |
| `AttackDamage` | `float` | ✅ | ✅ ¹ | 속성 `attack_damage` |
| `Armor` | `float` | ✅ | ✅ ¹ | 속성 `armor` |
| `Fire`, `Air` | `int` | ✅ | ✅ | NBT |
| `NoAI`, `Silent`, `Glowing`, `Invulnerable`, `NoGravity`, `PersistenceRequired` | `bool` | ✅ | ✅ | NBT |
| `uuid` / `UUID` | `int[]` | 출력만 | ❌ | NBT `UUID` |
| `Position` | `Position` | ✅ | ❌ | 엔티티 위치 |

- **소환 전 설정:** 소환 전에 쓴 값은 summon 직후 순서대로 적용됩니다. `maxHealth`를 `Health`보다 먼저 설정하세요.
- **¹ 속성(attribute):** 상수는 모든 버전에서 됩니다. 실행 중의 값은 함수 매크로가 필요해서 1.20.2 이상에서만 됩니다 (`#if MC_1_20_2_OR_GREATER`).

메서드는 `Kill()`, `Teleport(Position pos)`가 있습니다. 엔티티를 메서드에 넘길 수도 있습니다 (`void Buff(Entity e)`).

---

## BlockLib

마인크래프트의 모든 블록에 대해 게임 데이터로부터 생성된 전용 클래스가 있고, **블록 상태가 타입이 있는 속성으로 들어 있습니다.**

```csharp
use BlockLib;

new Block : OakStairs stairs;                // = OakStairs stairs = new OakStairs();
stairs.Facing = Facing.East;                 // 열거형
stairs.Half = Half.Top;
stairs.Waterlogged = false;                  // bool
m.SetBlockObject(stairs, origin);

Wheat crop = new Wheat { Age = 7 };          // 객체 이니셜라이저, int 상태
var lamp = new RedstoneLamp { Lit = true };

Block glass = new Block("minecraft:glass");  // ID 문자열로 만드는 일반 블록
m.FillBlockObject(glass, origin, origin.Offset(2, 0, 2));
if (m.IsBlockObject(stairs, origin)) { ... }
```

| 메서드 | 설명 |
|---|---|
| `m.SetBlockObject(Block b, Position pos)` | 상태를 포함해 블록 설치 |
| `m.FillBlockObject(Block b, Position from, Position to)` | 영역 채우기 |
| `m.IsBlockObject(Block b, Position pos)` | `pos`에 이 블록(이 상태)이 있는지 확인 |

- **클래스 이름:** ID를 PascalCase로 바꾼 이름입니다 (`oak_stairs` → `OakStairs`, `redstone_lamp` → `RedstoneLamp`).
- **상태:** 타입은 `bool`, `int`, 또는 상태 이름을 딴 열거형(`Facing`, `Half`, `Shape`, `Axis`, …)입니다. 블록에 없는 상태는 C# 타입 오류입니다. 잘못된 값이나 대상 버전에 없는 상태는 MC# 오류입니다.
- **컴파일 타임 값:** 블록 객체는 명령어 안에 그대로 컴파일되므로, 상태는 상수여야 합니다.
- **버전 검사:** 일부 대상 버전에만 있는 블록(예: `PaleOakLog`, 1.21.4+)은 해당 버전을 표시한 오류를 냅니다. `#if`를 쓰세요.

---

## PlayerLib

```csharp
use PlayerLib;
```

### 플레이어 선택

| 표현식 | 셀렉터 |
|---|---|
| `Player.All` | `@a` |
| `Player.Self` | `@s` (코드를 실행하는 플레이어, 예: 버튼 콜백 안) |
| `Player.Nearest` | `@p` |
| `Player.Random` | `@r` |
| `Player.Named("Steve")` | `@a[name=Steve]` |
| `Player.WithTag("vip")` | `@a[tag=vip]` |

`Player` 값은 변수에 담거나 메서드에 넘길 수 있습니다 (`void Reward(Player p)`).

### 속성

| 속성 | 타입 | 읽기 | 쓰기 |
|---|---|:-:|:-:|
| `Count` | `int` | ✅ | |
| `Exists` | `bool` | ✅ | |
| `Health` | `float` | ✅ | |
| `Food` | `int` | ✅ | |
| `Level` | `int` | ✅ | ✅ |
| `SelectedSlot` | `int` | ✅ | |
| `GameMode` | `GameMode` | | ✅ |
| `Position` | `Position` | ✅ | |

값을 읽는 속성은 조건에 맞는 첫 번째 플레이어의 값을 사용합니다.

### 메서드

| 메서드 | 설명 |
|---|---|
| `SendMessage(text)`, `ActionBar(text)`, `Title(title, subtitle?)` | 메시지 (보간 문자열, `Button`, `Text` 사용 가능) |
| `PlaySound(id, volume, pitch)` | 플레이어 위치에서 소리 재생 |
| `Teleport(pos)`, `Kill()`, `SetSpawn(pos)` | |
| `AddTag(tag)`, `RemoveTag(tag)`, `HasTag(tag)` | |
| `AddLevels(n)`, `AddXp(n)` | |
| `Effect(id, seconds, amplifier = 0, hideParticles = false)`, `ClearEffect(id)`, `ClearEffects()` | |
| `Run(Method)` | **각 플레이어로서** `Method`를 한 번씩 실행 (`void Method()` 또는 `void Method(Player p)`) |

### 인벤토리

칸 번호: **0–8 핫바, 9–35 주 인벤토리**, 그리고 `Slot.Head`, `Slot.Chest`, `Slot.Legs`, `Slot.Feet`, `Slot.Offhand`.

| 메서드 | 설명 |
|---|---|
| `Give(item, count = 1)` | 아이템 지급 |
| `ClearInventory()` / `ClearItem(item, count = -1)` | 전부 비우기 / 특정 아이템만 제거 |
| `CountItem(item)` → `int`, `HasItem(item)` → `bool` | 아이템 개수 세기 / 보유 확인 |
| `SetSlot(slot, item, count = 1)` | **특정 칸에 아이템 넣기** |
| `ClearSlot(slot)` | 칸 비우기 |
| `GetSlotCount(slot)` → `int` | 칸의 아이템 개수 |
| `IsSlot(slot, item)` → `bool`, `IsSlotEmpty(slot)` → `bool` | 칸 확인 |
| `CopySlot(from, to)`, `MoveSlot(from, to)`, `SwapSlots(a, b)` | 칸 복사 / 이동 / **맞바꾸기** |

```csharp
Player p = Player.All;
p.SetSlot(0, "minecraft:diamond_sword");
p.SetSlot(Slot.Head, "minecraft:iron_helmet");
p.SwapSlots(0, 8);
if (p.IsSlotEmpty(Slot.Offhand)) p.SetSlot(Slot.Offhand, "shield");
```

- **실행 중 값:** 개수, 초, 레벨, `SetSlot`/`ClearSlot`의 칸 번호(0–35)는 1.20.2 이상에서 실행 중 값도 쓸 수 있습니다 (매크로).
- **버전별 명령어:** 칸 검사는 1.20.5 이상에서는 `execute if items`, 이전 버전에서는 NBT를 사용합니다. 컴파일러가 자동으로 고릅니다.

---

## TuiLib

`tellraw` JSON을 직접 쓰지 않고 채팅 UI를 만듭니다.

```csharp
use PlayerLib;
use TuiLib;

static int votes = 0;

public static void Main() {
    Button yes = new Button("[예]", OnYes) { Color = Color.Green, Hover = "찬성" };
    Button no = new Button("[아니오]", OnNo) { Color = Color.Red };
    Tui.Menu(Player.All, new Text("MC#이 마음에 드나요?") { Bold = true, Color = Color.Gold }, yes, no);
}

public static void OnYes(Player p) {        // 클릭한 플레이어로서 실행됩니다
    votes++;
    p.SendMessage($"감사합니다! 득표 = {votes}");
}

public static void OnNo() {
    Player.Self.ActionBar("다음에 다시 물어볼게요");
}
```

| API | 설명 |
|---|---|
| `new Button(label, Method)` | 클릭할 수 있는 버튼. `Method`는 `void M()` 또는 `void M(Player clicker)` |
| `new Text(content)` | 스타일 텍스트 |
| `.Color`, `.Bold`, `.Italic`, `.Underlined`, `.Hover` | 스타일 (객체 이니셜라이저 또는 대입) |
| `Tui.Print(parts...)` / `Tui.PrintTo(player, parts...)` | 문자열·값·버튼·텍스트로 이루어진 채팅 한 줄 |
| `Tui.Menu(player, title, buttons...)` | 제목 줄 + 버튼 한 줄 |
| `Tui.Title(player, title, subtitle?)`, `Tui.ActionBar(player, text)` | 타이틀 / 액션바 |
| `Tui.Clear(player)` | 채팅 지우기 |

동작 방식:
- **클릭:** 버튼은 `/trigger`(objective `mcs.<ns>.btn`)를 사용하므로 **OP 권한이 없는 플레이어도 클릭할 수 있습니다.** 틱 함수가 클릭을 감지해 지정한 메서드를 호출합니다.
- **버전별 컴포넌트:** 텍스트 컴포넌트 형식은 버전마다 다르지만(1.21.5 이상에서 `clickEvent` → `click_event`) 컴파일러가 알맞은 형식으로 출력합니다.

---

## 라이브러리 확장하기

내장 선언은 [`src/Mcs.Compiler/Prelude.cs`](../../src/Mcs.Compiler/Prelude.cs)에 있습니다. 블록 클래스와 `EntityType`은 [`McData.cs`](../../src/Mcs.Compiler/McData.cs)의 `PreludeGen`이 데이터 파일 `src/Mcs.Compiler/Data/mcdata.json`으로 생성합니다.

| 어트리뷰트 | 의미 |
|---|---|
| `[Nbt(path, type)]` | 엔티티 NBT 경로 읽기/쓰기 |
| `[McAttribute(id)]` | 엔티티 속성(attribute) (`generic.` 없이 작성) |
| `[McId(id)]` | 블록 클래스 / 열거형 값의 마인크래프트 ID |
| `[State(name, values)]` | 블록 상태 속성 |
| `[Intrinsic(name)]` | 컴파일러가 직접 처리 (`Codegen.cs` / `Codegen.Libs.cs`) |

### 게임 데이터 갱신

새 마인크래프트 버전이 나오면:

1. 버전마다 데이터 생성기를 실행합니다: `java -DbundlerMainClass=net.minecraft.data.Main -jar server.jar --reports --output <gen>/<ver>/out`
2. `tools/probe_ids.sh`로 기능 플래그가 걸린 ID를 찾습니다. EULA에 동의한 서버 폴더가 필요합니다.
3. `python3 tools/gen_mcdata.py <gen>`을 실행합니다.
