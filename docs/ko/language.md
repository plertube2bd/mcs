# MC# 언어 가이드

기본 변수와 제어문을 넘어서는 언어 기능을 설명합니다. 큰 프로젝트에서 이 기능들을 쓰면 되고, [`~/Desktop/mcw/mcs`](../../../mcw/mcs)(전략 게임 하나 전체)는 전부 이 기능들로 작성되어 있습니다.

## 엔티티 클래스

엔티티 클래스를 쓰면 엔티티를 필드와 메서드가 있는 객체로 다룰 수 있습니다.

```csharp
[Entity("minecraft:marker", "war.prov")]          // 엔티티 종류 + 인스턴스를 구분하는 태그
public partial class Province : EntityObject
{
    [Score("war.terr")] public int Terr;           // 필드 하나 = 스코어보드 objective 하나
    [Score("war.pop", Display = "인구", Color = "white")] public int Pop;
    public int Owner;                              // 기본 objective: <네임스페이스>.owner

    public void Claim(int nation)                  // 인스턴스 메서드: 그 엔티티(@s)로서 실행
    {
        Owner = nation;
        AddTag("owned");
    }
}

[Entity("minecraft:player")]
public partial class Nation : Player               // 플레이어: Player를 상속하면 Player의 메서드를 그대로 사용
{
    [Score("war.act", Criteria = "trigger")] public int Act;
}
```

| 요소 | 컴파일 결과 |
|---|---|
| 필드 읽기/쓰기 `p.Terr` | 그 엔티티의 스코어: `scoreboard players ... <셀렉터> war.terr` |
| `[Score(name, Display, Color, Criteria)]` | load 함수에서 objective를 자동으로 생성 |
| 인스턴스 메서드 호출 `p.Claim(1)` | `execute as <p> at @s run function ...` (`p`가 이미 `@s`면 `function ...`) |
| `this`, 암시적 필드 | `@s` |
| 엔티티 매개변수 `void F(Province p)` | 엔티티에 태그를 붙여서 전달 |
| 엔티티 반환값 `Province Find(...)` | 태그로 반환. 찾지 못하면 `null` |
| `p == null` / `p != null` | `execute if entity <p>` |

모든 엔티티에 기본으로 있는 멤버: `AddTag`, `RemoveTag`, `HasTag`, `Kill`, `Merge(snbt)`, `Position`, `Teleport(pos)`, `Run(() => ...)`

`partial` 클래스는 여러 파일에 나눠 쓸 수 있습니다.

## 엔티티 조회

```csharp
foreach (var p in Entities.All<Province>())                        // execute as @e[type=marker,tag=war.prov]
foreach (var u in Entities.All<Unit>("scores={war.st=0}", at: true)) // + 셀렉터 필터, + at @s
var p = Entities.Nearest<Province>(new Position(x, 101, z), 0.5);   // 0.5칸 안에서 가장 가까운 것
var r = Entities.Random<Province>("tag=war.capc");
var n = Entities.Spawn<Province>(Position.Here);
int c = Entities.Count<Province>("tag=war.land");
bool any = Entities.Any<Unit>("tag=war.ba");
var me = Entities.Self<Nation>();                                    // @s
```

- **반복 본문:** 엔티티 `foreach`는 각 엔티티로서 본문을 한 번씩 실행합니다. `continue`와 `return`은 쓸 수 있고, `break`는 쓸 수 없습니다.
- **중첩:** 반복 변수, `this`, 매개변수는 중첩된 반복이나 람다 안에서도 그대로 유효합니다. 필요할 때 컴파일러가 엔티티에 태그를 붙입니다.
- **조회 결과:** `Nearest`, `Random`, `First`, `Spawn`의 결과는 태그로 붙잡아 두므로, 실행 위치나 대상이 바뀌어도 계속 유효합니다.

## 컴파일 시간 코드 (코드 생성기 대체)

```csharp
static readonly string[] Banner = { "", "red", "blue" };       // 표 (컴파일 타임 상수)
static readonly int[] Cost = { 0, 20, 15 };

[Inline] static void Sb(int dx, int dy, int dz, string block)  // 호출한 자리에 펼쳐짐
{
    asm $"setblock ~{dx} ~{dy} ~{dz} minecraft:{block}"
}

foreach (int y in Unroll.Range(0, 3)) Sb(0, y, 0, "stone");    // 펼쳐짐: setblock 3줄
foreach (string c in Banner) asm $"team add t_{c}"            // 배열도 펼쳐짐
```

- **상수 인자:** `[Inline]` 메서드는 함수로 만들어지지 않고, 본문이 호출한 자리에 복사됩니다. 상수로 넘긴 인자는 계속 상수로 남기 때문에, 그 값에 대한 `if`, `switch`, `?:`, 문자열 비교, 반복문은 컴파일할 때 결정됩니다. `string` 매개변수가 있는 메서드는 반드시 `[Inline]`이어야 합니다.
- **반환값:** `[Inline]` 메서드는 마지막 문장에서 값을 반환할 수 있습니다. `string`, `Text`, `Button`도 반환할 수 있습니다.
- **표:** `static readonly` 배열은 표로 쓰입니다. 상수 인덱스(`Cost[2]`)는 상수가 됩니다. 실행 중 인덱스(`Cost[b]`)는 매크로로 스토리지에서 읽고(1.20.2+), 텍스트나 `asm $""` 안의 문자열 표에도 쓸 수 있습니다.

## switch

```csharp
switch (bld)
{
    case 1: Res(); break;
    case 2: case 3: Mine(); break;
    default: None(); break;
}
```

- **정수와 열거형:** `switch`는 정수와 열거형에 쓸 수 있고, case 하나마다 `execute if score ... matches` 한 줄이 됩니다.
- **상수 값:** 상수에 대한 `switch`(예: `[Inline]` 메서드 안)는 맞는 case만 남깁니다.
- **`break`:** 각 섹션의 마지막 문장으로만 쓸 수 있습니다.

## 실행 중 값이 들어가는 명령어

| 기능 | 설명 |
|---|---|
| `asm $"tp @s {x} 100 {z}"` | 보간 명령어. 상수는 그대로 치환되고, 실행 중 값은 매크로로 들어갑니다 (1.20.2+) |
| `m.Check("block ~ ~ ~ minecraft:stone")` | `execute if` 조건을 그대로 사용. `if`, `&&`, `!`에 쓸 수 있음 |
| `int n = m.Result("data get entity @s Pos[0]")` | 명령어의 결과값 (`execute store result`). 보간 가능 |
| `bool ok = m.Success("...")` | 명령어 성공 여부 |
| `m.Random(0, 99)` | `random value 0..99` |
| `m.Schedule(Method, 20)` / `m.ClearSchedule(Method)` | `schedule function` |
| `Exec.Run("anchored eyes positioned ^ ^ ^", Step)` | 임의의 execute 하위 명령 + 람다 또는 메서드 |
| `Exec.At(pos, () => { ... })` | 해당 위치에서 실행 (좌표에 실행 중 값 사용 가능) |
| `new Position(x, y, z)` | 좌표에 실행 중 값 사용 가능 (매크로) |

## 명령어를 실행하는 채팅 버튼

`new Button("[건설]", "trigger war.act set 3")`은 지정한 명령어를 실행하는 버튼을 만듭니다. `new Button(label, Method)`는 MC# 메서드를 호출하는 버튼을 만듭니다.

## 리소스

함수가 아닌 파일(월드 프리셋, 블록 태그 등)은 폴더 하나에 모아 두고 `mcs.json`의 `"resources"`에 지정하세요. 데이터팩에 그대로 복사됩니다.

```json
{ "namespace": "war", "sources": ["src"], "resources": "res", "minecraft": "1.21.9-26.3" }
```
