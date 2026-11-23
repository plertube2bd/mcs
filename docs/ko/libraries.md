# MC# 라이브러리 레퍼런스

MC#에는 내장 라이브러리가 다섯 개 있습니다. 모두 데이터팩 안에 함께 컴파일되므로, 출력된 팩은 다른 데이터팩이나 모드에 의존하지 않습니다.

| 라이브러리 | 불러오는 방법 | 내용 |
|---|---|---|
| **Core** | 항상 사용 가능 | `asm`, `m.Console`, `Position`, `GetWorldSpawn`, `SetWorldSpawn`, `SpawnEntity`, `m.SetBlock`/`FillBlock`/`IsBlock`, `[Load]`, `[Tick]` |
| **EntityLib** | `use EntityLib;` | `Entity`, `EntityType`, `m.SpawnEntityObject` |
| **DisplayLib** | `use DisplayLib;` | `BlockDisplay`, `ItemDisplay`, `TextDisplay` (디스플레이 엔티티: 크기, 회전, 애니메이션, 실행 중 텍스트) |
| **BlockLib** | `use BlockLib;` | `Block`과 블록마다 하나씩 있는 클래스(`OakStairs`, `Stone`, …), `m.SetBlockObject`/`FillBlockObject`/`IsBlockObject` |
| **PlayerLib** | `use PlayerLib;` | `Player` (제어, 인벤토리 칸, 효과 등), `Slot`, `GameMode` |
| **TuiLib** | `use TuiLib;` | 클릭할 수 있는 채팅 `Button`, 스타일 `Text`, `Tui.Menu`/`Print`/`Title` |
| **MathLib** | `use MathLib;` | `Math.Sin`/`Cos`/`Tan`, `Math.FloorDiv`/`FloorMod`, `Math.PI`/`Tau`/`Deg2Rad`/`Rad2Deg` |
| **ItemLib** | `use ItemLib;` | 아이템 오브젝트(`new Item(id) { Name = … }`), `m.GiveObject`, `[OnRightClickObject]` |
| **GuiLib** | `use GuiLib;` | `Gui.SetDisplay`/`ClearDisplay`, `Board`(사이드바 / 탭 목록 / 이름 아래), `BossBar` |

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
- **최상위**(메서드 밖)에 쓴 `asm`은 load 시 소스 순서대로, `[Load]` 메서드와 `Main`보다 먼저 실행됩니다. `m.Mount`가 쓸 objective를 만들 때 유용합니다.
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
| `float` | `-12.345` (소수점 3자리) |
| `double` | `-12.34567` (소수점 5자리) |
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

### `m.Mount` — 변수를 기존 스코어에 연결

```csharp
asm scoreboard objectives add test dummy      // 직접 또는 다른 데이터팩이 만든 objective

static int hello;
m.Mount(hello, "test.hello");                 // hello ⇄ objective "test"의 "hello" 점수

public class test : dummy {}                  // 스코어보드 클래스: objective 하나
m.Mount(test, "test");                        // test.<이름> ⇄ "test"의 "<이름>" 점수

public static void Main()
{
    hello += 10;                              // scoreboard players add hello test 10
    test.hello = 0;                           // `hello`와 같은 점수
    test.coins = test.coins + 5;              // 처음 쓸 때 필드가 자동으로 선언됨
}
```

| 형태 | 의미 |
|---|---|
| `m.Mount(필드, "objective.holder")` | `static int` / `bool` 필드를 연결. 문자열은 **마지막** `.`에서 나눔 |
| `m.Mount(필드, "objective", "holder")` | 같음. holder 이름에 `.`이 들어갈 때 사용 |
| `public class X : dummy {}` | 스코어보드 클래스. `static int`/`bool` 필드 하나가 objective 안의 holder(필드 이름) 하나. `X.이름`을 쓰면 `public static int 이름`이 자동 선언됨 (`bool` 필드는 직접 선언) |
| `m.Mount(X, "objective")` | 클래스의 objective 지정. 없으면 `<ns>.<x>` |

- **컴파일 타임 + 전역:** Mount는 선언입니다. 최상위든 메서드 안이든 어디에 쓰든 프로그램 전체의 해당 필드 사용에 적용되고, 명령어는 생성하지 않습니다.
- **objective 생성:** 스코어보드 클래스는 load 시 objective를 만듭니다 (`scoreboard objectives add <obj> dummy`, 이미 있으면 무해). 필드 Mount(`"test.hello"`)는 아무것도 만들지 않으므로 objective가 있어야 합니다.
- **초깃값:** 연결된 필드는 초기화되지 않습니다. `static int x = 5;`는 점수가 아직 없을 때만 설정합니다.
- `static int` / `bool` 필드만 가능합니다 (스코어는 정수).

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

## DisplayLib

블록·아이템·텍스트 **디스플레이 엔티티**입니다. 움직이거나 충돌하거나 틱마다 동작하지 않는 모델과 떠 있는 글자입니다. 엔티티이므로 [EntityLib](#entitylib)의 기능(`m.SpawnEntityObject`, `Kill()`, `Teleport()`, `Glowing`, `Clone()`, [한 번만 사용 규칙](language.md#오브젝트-한-번만-사용))도 그대로 됩니다.

```csharp
use DisplayLib;
use BlockLib;

var label = new TextDisplay("Hello") { Billboard = Billboard.Center, Background = 0x40000000 };
m.SpawnEntityObject(label, Position.Here.Offset(0, 2, 0));
label.Text = $"Score: {score}";              // 실행 중 값이 글자에 들어감 (1.20.2+)

new Block : OakStairs stairs;
stairs.Facing = Facing.East;
var model = new BlockDisplay(stairs) { Scale = 0.5f, Brightness = 15 };
m.SpawnEntityObject(model, Position.Here);
model.RotationY = 90f;                       // 도 단위
model.Scale = 2f;
model.Animate(20);                           // 위의 두 변경이 20틱 동안 애니메이션됨

var gem = new ItemDisplay("diamond") { Mode = ItemDisplayMode.Ground };
```

생성자 인자는 내용(`Text`, `Block`, `Item`)을, `{ … }` 초기화는 다른 속성을 정합니다. 둘 다 다른 소환 전 설정처럼 소환 직후에 적용됩니다.

**모든 디스플레이**

| 속성 | 타입 | NBT | |
|---|---|---|---|
| `Billboard` | `Billboard` (`Fixed`, `Vertical`, `Horizontal`, `Center`) | `billboard` | 플레이어 쪽으로 회전 |
| `Scale` / `ScaleX`, `ScaleY`, `ScaleZ` | `float` | `transformation.scale` | 균일 / 축별 |
| `OffsetX`, `OffsetY`, `OffsetZ` | `float` | `transformation.translation` | 모델 위치 이동 (블록 단위) |
| `RotationY`, `RotationX` | `float` (도, 쓰기 전용) | `transformation.left_rotation` / `right_rotation` | Y축(위에서 볼 때 반시계), 그다음 X축 회전 |
| `Brightness` | `int` 0–15 | `brightness` | 고정 밝기 (블록·하늘 빛) |
| `GlowColor` | `int` (RGB) | `glow_color_override` | `Glowing = true`일 때 테두리 색 |
| `ViewRange`, `ShadowRadius`, `ShadowStrength`, `Width`, `Height` | `float` | 같은 이름 | |
| `InterpolationDuration`, `InterpolationDelay` | `int` 틱 | `interpolation_duration`, `start_interpolation` | |
| `TeleportDuration` | `int` 틱 | `teleport_duration` | 부드러운 순간이동 (1.20.2+) |

`Animate(int ticks)`는 이번 틱에 바꾼 변환(`Scale`, `Offset…`, `Rotation…`)을 `ticks`틱 동안 애니메이션합니다.

**TextDisplay**: `Text`(문자열, 쓰기 전용), `LineWidth`(`int`), `Background`(`int` ARGB, 예: `0x40000000`), `TextOpacity`(`int` 0–255), `Shadow`, `SeeThrough`, `DefaultBackground`(`bool`), `Alignment`(`TextAlignment.Center/Left/Right`).
**BlockDisplay**: `Block`([블록 오브젝트](#blocklib), 쓰기 전용. 생성자는 `"oak_stairs[facing=east]"` 같은 ID 문자열도 받음).
**ItemDisplay**: `Item`(아이템 ID, 쓰기 전용), `Mode`(`ItemDisplayMode`: `None`, `Head`, `Gui`, `Ground`, `Fixed`, `ThirdPersonLeftHand`, …).

- **실행 중 텍스트:** 텍스트 디스플레이는 점수 컴포넌트를 계산하지 않으므로, `$"…{x}…"`를 현재 값을 글자에 직접 넣는 함수 매크로로 바꿉니다 (숫자 형식은 `m.Console.WriteLine`과 같음). 1.20.2+가 필요하고, 상수 텍스트는 모든 버전에서 됩니다. 대입한 시점의 값이 표시되니, 갱신하려면 다시 대입하세요.
- **실행 중 회전값**(`model.RotationY = angle;`)도 매크로를 씁니다 (1.20.2+). 다른 속성은 모든 버전에서 실행 중 값을 쓸 수 있습니다.
- **버전:** 텍스트는 JSON(≤1.21.4) / SNBT(1.21.5+), 아이템은 `Count`(≤1.20.4) / `count`, 블록 상태는 `{Name, Properties}`(≤26.2) / `{id, properties}`(26.3+)로 버전마다 알맞게 씁니다.
- 숫자 속성은 읽을 수도 있습니다 (`model.ScaleX`, `label.LineWidth` 등). `Text`, `Block`, `Item`, 회전은 쓰기 전용입니다.

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

## MathLib

```csharp
use MathLib;

double a = 30 * Math.Deg2Rad;
double s = Math.Sin(a);          // 0.5
float  c = Math.Cos(1.0f);       // float 오버로드 → float 결과
```

| 멤버 | 설명 |
|---|---|
| `Math.Sin(x)`, `Math.Cos(x)`, `Math.Tan(x)` | 라디안 단위. `double`, `float` 오버로드 |
| `Math.PI`, `Math.Tau`, `Math.Deg2Rad`, `Math.Rad2Deg` | 상수 (컴파일 타임에 계산) |
| `Math.FloorDiv(a, b)`, `Math.FloorMod(a, b)` | `int` 내림 나눗셈 / 제수 부호를 따르는 나머지. 각각 스코어보드 명령어 하나 (`/`, `%` 연산자는 C#을 따릅니다 — [언어 가이드](language.md#나눗셈과-나머지) 참고) |

동작 방식:
- **수 형식:** `double`은 Q15.16 고정소수점입니다 (스코어 = 값 × 65536, 정밀도 1/65536, 범위 약 ±32768). `float`은 그대로 ×1000입니다. 섞어 쓰면 자동 변환되고, `double` → `float` 대입은 C#처럼 캐스트가 필요합니다.
- **테일러 급수가 아닌 룩업 테이블:** 각도를 2π로 나눈 나머지로 줄인 뒤 1024등분 원 위의 위치로 바꾸고, 257칸짜리 1/4 주기 사인 테이블(나머지 사분면은 대칭)에서 찾은 다음 이웃 칸 사이를 선형 보간합니다. 테이블은 `execute if score … matches` 함수의 이진 트리라서 모든 버전에서 동작합니다 (매크로 불필요). 전체 범위에서 오차는 약 2/65536 이내입니다.
- **26.3+ (`/compute`):** 아래 연산들이 숫자 제공자(number provider) 식을 계산하는 명령 하나로 바뀝니다. `double *`(약 5배 빠름), `double /`(나누는 수가 128.0 미만이면 약 11배, 더 크면 기존 긴 나눗셈), `int` `/`·`%`(약 2.7배), `Sin`/`Cos`/`Tan`(약 13배). `*`, `/`, `%` 결과는 이전 버전과 비트 단위로 같습니다. 삼각함수는 게임 내장 65536칸 사인 표에 1차 보정을 더해 조금 더 정확하고(최대 오차 약 1.8/65536 → 1.6/65536), 그래서 마지막 자리가 이전 버전과 다를 수 있습니다. 차이 하나: 결과가 범위(±32768)를 넘는 `double` 곱셈은 26.3에서 순환값 대신 0이 됩니다.
- `Cos(x)`는 같은 테이블을 1/4 바퀴 민 것이고, `Tan(x)`는 Q15.16 나눗셈으로 구한 `Sin/Cos`이며 ±π/2 근처에서는 ±32767.99로 포화됩니다.
- 인자가 상수면 컴파일 타임에 계산합니다.
- `double * double`, `double / double`은 32비트를 넘는 중간값이 필요해서 공용 헬퍼(`<ns>:__math/dmul`, `__math/ddiv`)를 호출합니다. 곱셈은 피연산자를 16비트씩 나눠 계산하고, 나눗셈은 16단계 긴 나눗셈을 합니다. 곱셈은 내림, 나눗셈은 C#처럼 0 방향 버림이며 둘 다 정확합니다. 결과가 범위를 넘는 나눗셈은 ±2147483647/65536으로 포화됩니다. 정수 상수로 곱하거나 나누면 스코어보드 연산 한 번으로 끝납니다.
- 헬퍼와 테이블은 실제로 쓸 때만 생성됩니다.

## ItemLib

```csharp
use ItemLib;
use PlayerLib;

static Item wand = new Item("carrot_on_a_stick") { Name = "Magic Wand", Lore = "Right-click me\nfor sparks", Glint = true };

[OnFirstJoin] static void Kit(Player p) { m.GiveObject(wand.Clone(), p); }   // 플레이어마다 복사본 지급
[OnRightClickObject(nameof(wand))] static void Zap(Player p) { p.SendMessage("Zap!"); }
```

| 멤버 | 설명 |
|---|---|
| `new Item(id)` | 아이템 오브젝트. 속성(컴파일 타임 상수): `Name`, `Lore`(`\n` = 줄바꿈), `Unbreakable`, `Glint` |
| `m.GiveObject(item, players, count = 1)` | `/give` — 오브젝트를 **소비**합니다 ([오브젝트](language.md#오브젝트-한-번만-사용) 참고) |
| `item.Clone()` | 내용과 **정체성(id)이 같은** 새 오브젝트 |
| `item.Dispose()` | 오브젝트 폐기 |
| `[OnRightClickObject(nameof(field))]` | 이벤트: 이 아이템(또는 그 복사본)을 들고 우클릭 |

- **정체성:** 아이템 오브젝트마다 custom data `mcs_obj:"<ns>:<필드>"`가 들어갑니다. 복사본도 같은 값이라 `[OnRightClickObject]`는 모든 복사본에 반응합니다.
- **버전:** 1.20.1–1.20.4는 NBT(`{display:{Name:…}}`), 1.20.5+는 아이템 컴포넌트, 1.21.5+는 텍스트 컴포넌트를 SNBT로 씁니다. 감지는 1.20.5 이전 `nbt={SelectedItem:…}`, 이후 `execute if items`입니다.
- **`carrot_on_a_stick` / `warped_fungus_on_a_stick` 우클릭**은 모든 버전에서 됩니다 (사용 통계). 사용된 아이템은 게임처럼, 주 손이 그 아이템 종류면 주 손, 아니면 보조 손입니다.
- **다른 아이템 우클릭**은 **1.20.5+**가 필요합니다 (빌드 범위에 이전 버전이 있으면 컴파일 에러 — `--mc 1.20.5-…`로 빌드). 아이템에 끝나지 않는 사용 동작(1.20.5–1.21.1은 `food` 컴포넌트, 1.21.2+는 소리 없는 `consumable`)을 붙이고, `using_item` 발전과제로 핸들러를 실행합니다. 버튼을 누르고 있으면 한 번으로 셉니다. 한계: 1.20.5–1.21.1에서는 누르는 동안 먹는 동작과 소리가 납니다. 자체 우클릭 동작이 있는 아이템(활, 방패, 스폰 알 등)은 그 동작이 우선이고, 블록 아이템은 블록을 겨누면 설치됩니다.

```csharp
// @mc 1.20.5-26.3
static Item staff = new Item("stick") { Name = "Staff" };
[OnRightClickObject(nameof(staff))] static void Cast(Player p) { p.SendMessage("Cast!"); }
```
- 정적 아이템은 load마다 새로 만들어집니다 (`m.GiveObject(wand, …)`는 load당 한 번). 지역 아이템은 선언문이 실행될 때마다 만들어집니다.

---

## GuiLib

```csharp
use GuiLib;
use PlayerLib;

static Board stats = new Board("stats", "Stats");      // objective <ns>.board.stats
static BossBar timer = new BossBar("timer", "Time");   // 보스바 <ns>:timer

public static void Main()
{
    stats.Set("Kills", 0);
    stats.Show();                                       // 사이드바
    Gui.SetDisplay(DisplaySlot.BelowName, "my.objective");

    timer.Max = 60;
    timer.Color = BarColor.Red;
    timer.Show(Player.All);
    for (int t = 60; t > 0; t--)
    {
        timer.Value = t;
        timer.Title = $"Time: {t}";
        Wait(20);
    }
    timer.Hide();
}
```

### `Gui`

| 메서드 | 명령어 |
|---|---|
| `Gui.SetDisplay(DisplaySlot slot, string objective)` | `scoreboard objectives setdisplay <slot> <objective>` (아무 objective나 가능. 예: `[Score]` 필드의 `<ns>.<필드>`) |
| `Gui.SetDisplay(DisplaySlot slot, Board board)` | 보드를 슬롯에 표시 |
| `Gui.ClearDisplay(DisplaySlot slot)` | 슬롯 비우기 |

`DisplaySlot`: `Sidebar`, `List`(탭 목록), `BelowName`(1.20.1에서는 `belowName`, 1.20.2 이상에서는 `below_name`으로 출력).

### `Board`

자체 dummy objective `<ns>.board.<id>`를 가진 스코어보드 표시판이며 load 시 생성됩니다. id가 같으면 같은 보드이므로 어느 메서드에서든 `new Board("stats")`로 같은 보드를 가리킵니다. `static` 필드로 둬도 됩니다.

| 멤버 | 설명 |
|---|---|
| `new Board(id, title = id)` | `id`: 소문자, 숫자, `_ . -`. `title`: 문자열 상수 |
| `Title = "..."` | 표시 이름 변경 |
| `Show(slot = Sidebar)` / `Hide(slot = Sidebar)` | 슬롯에 표시 / 슬롯 비우기 |
| `Set(string line, int value)` | 사이드바 줄 설정 (공백 없는 가짜 플레이어 이름). 값은 런타임 값도 가능 |
| `Set(Player players, int value)` | 플레이어 점수 설정 (`List` / `BelowName`에 표시되는 값) |
| `Get(string line)` | 줄의 값 읽기 |
| `Remove(string line)` / `Clear()` | 줄 하나 / 모든 줄 삭제 |

### `BossBar`

보스바 `<ns>:<id>`이며 load 시 생성됩니다 (제목은 load마다 갱신). id가 같으면 같은 보스바입니다.

| 멤버 | 설명 |
|---|---|
| `new BossBar(id, title = id)` | `title`: 문자열 상수 |
| `Value`, `Max` | 읽기/쓰기. 런타임 값은 `execute store result bossbar`로 기록 |
| `Title = ...` | 텍스트. 보간 값(`$"Time: {t}"`)은 설정하는 시점의 값으로 채워짐 |
| `Color = BarColor.Red` | `Blue Green Pink Purple Red White Yellow` |
| `Style = BarStyle.Notched10` | `Progress Notched6 Notched10 Notched12 Notched20` |
| `Visible = ...` | 상수 또는 런타임 `bool` |
| `Show(Player players)` / `Hide()` | 보이는 플레이어 지정 / 모두에게서 숨김 |
| `Remove()` | 보스바 삭제 (다음 `/reload`에서 다시 생성) |

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
