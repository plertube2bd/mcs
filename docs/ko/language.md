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

## 숫자 타입: Milli와 Q16

MC#의 숫자는 스코어이므로 소수는 고정소수점입니다. MC# 이름이 실제 표현을 그대로 나타냅니다:

| 타입 | 저장 방식 | 정밀도 | 범위 |
|---|---|---|---|
| `int` | 스코어 | 1 | ±2147483647 |
| `Milli` | 스코어 ×1000 | 0.001 | ±2147483.647 |
| `Q16` | 스코어 ×65536 (Q15.16) | 1/65536 ≈ 0.0000153 | ±32768 |

```csharp
Milli speed = 1.25f;      // 리터럴은 C#처럼 f 접미사를 붙입니다
Q16 angle = 0.5;
Milli back = (Milli)(angle * 2);
```

`float`와 `double`은 같은 두 타입의 옛 이름입니다. 지금도 컴파일되지만 쓸 때마다 경고가 나오고, 앞으로 어느 버전에서든 삭제될 수 있습니다:

```
hello.mcs(3,8): warning MCS: 'float' is deprecated: use Milli. 'float' still works for now, but may be removed in any future version.
```

## 클래스

일반 클래스(와 구조체)는 C#처럼 동작합니다: 필드, 자동 속성, 생성자, 메서드, `this`, 상속, `virtual` / `abstract` / `override`, 출력용 `ToString()` 재정의.

```csharp
class Vec {
    public int X, Y;
    public Vec(int x, int y) { X = x; Y = y; }
    public int Len2() => X * X + Y * Y;
    public void Add(Vec o) { X += o.X; Y += o.Y; }
    public override string ToString() => $"({X}, {Y})";
}

static Vec origin = new Vec(0, 0);         // static 객체는 처음 로드될 때 한 번 만들어집니다

public static void Main() {
    var a = new Vec(3, 4);
    Vec b = a;                              // 같은 객체
    b.X = 10;
    a.Add(new Vec(1, 1));
    m.Console.WriteLine($"{a} {a.Len2()}");  // (11, 5) 146
}
```

컴파일 방식:

- **필드는 스코어입니다.** 객체의 필드는 스코어로 펼쳐지므로(`$main.a.X`) 필드 읽기·쓰기는 스코어보드 명령어 하나입니다. NBT도 매크로도 필요 없고 모든 버전에서 동작합니다. NString / NArray 필드는 storage에 둡니다.
- **메서드는 함수입니다.** 메서드마다 `this` 프레임이 있어서, `a.Add(o)`는 `a`를 프레임에 복사하고 `<ns>:vec/add`를 실행한 뒤 다시 복사해 옵니다(메서드가 객체를 바꿀 수 있을 때만). 생성자도 각각 함수입니다.
- **참조는 컴파일 타임에 정해집니다.** `Vec b = a;`는 `b`를 같은 객체로 만들고, 객체를 메서드에 넘기면 메서드가 그 객체를 바꿀 수 있습니다. 조건에 따라 변수가 다른 객체를 가리킬 수는 없습니다(`if (c) b = other;`는 오류 — 필드를 대입하세요). `null`도 없습니다.
- **필드 안의 객체는 내장됩니다.** `class Rect { public Vec Min; }`는 `Vec`의 필드를 `Rect` 안에 저장하고, `r.Min = v`는 `v`의 필드를 복사합니다. 그래서 클래스는 자기 자신의 타입을 담을 수 없습니다(연결 리스트 불가).
- **가상 호출은 컴파일 타임에** 객체의 실제 클래스로 정해집니다. 부모 클래스에서 물려받은 메서드는 클래스마다 한 벌씩 만들어지므로, 그 안의 호출도 해당 클래스의 재정의로 갑니다.
- `ToString()`은 식 하나(`=> $"…"`)여야 하고, 객체를 출력하는 자리에서 계산됩니다. 없으면 `Vec(X: 3, Y: 4)`처럼 출력됩니다.
- 지원하지 않는 것: 생성자 연결(`: this(…)` / `: base(…)`), `null`, 계산 속성(메서드를 쓰세요), 객체를 받거나 돌려주는 재귀 메서드.

## 제네릭

제네릭 메서드와 제네릭 클래스는 타입 인자 조합마다 한 번씩 컴파일됩니다(`Max<int>` → `<ns>:max_int`, `Box<Milli>` → `<ns>:box_milli/…`). 연산자는 .NET 제네릭 수학 인터페이스로 씁니다:

```csharp
using System.Numerics;

static T Max<T>(T a, T b) where T : INumber<T> => a > b ? a : b;

class Box<T> where T : INumber<T> {
    public T Value;
    public Box(T v) { Value = v; }
    public T Twice() => Value + Value;
}

m.Console.WriteLine($"{Max(3, 9)} {Max(1.5f, -2f)} {new Box<Q16>(0.25).Twice()}");   // 9 1.500 0.50000
```

- `T.Zero`, `T.One`, `default`를 쓸 수 있습니다. 타입 인자는 `int`, `bool`, `Milli`, `Q16`, 클래스, `NString`, `NArray<…>`가 될 수 있습니다.
- 재귀 제네릭 메서드는 지원하지 않습니다.

## 런타임 문자열과 배열: NString, NArray<T>

`string`은 컴파일 타임 상수입니다. 실행 중에 바뀌는 문자열은 **`NString`**, 실행 중에 길이가 바뀌는 배열은 **`NArray<T>`**(`T`: `int`, `bool`, `Milli`, `Q16`, `NString`)를 씁니다. 둘 다 NBT storage(`<ns>:mcs`)에 저장됩니다.

```csharp
NString name = "Steve";
int hp = 17;
NString line = $"{name} has {hp} HP";      // 실행 중에 이어 붙임
line += "!";
if (line.StartsWith("Steve")) m.Console.WriteLine(line);

var scores = new NArray<int> { 4, 9 };
scores.Add(hp);
scores[0] += 1;
int best = 0;
foreach (int s in scores) if (s > best) best = s;
m.Console.WriteLine($"{scores} best={best} n={scores.Length}");   // [5, 9, 17] best=17 n=3
```

| NString | | NArray<T> | |
|---|---|---|---|
| `"text"`, `$"… {x} …"`, `a + b`, `+=` | ① | `new NArray<T>()`, `new NArray<T>(n)`, `{ 1, 2 }` | |
| `==`, `!=` | | `a[i]`, `a[i] = v`, `a[i] += v` | ② |
| `Length`, `IsEmpty` | | `Length` / `Count` | |
| `Substring(start[, length])`, `s[i]` | ② | `Add(v)`, `Insert(i, v)` ②, `RemoveAt(i)` ②, `Pop()`, `Clear()` | |
| `StartsWith`, `EndsWith` | | `foreach` | |
| `IndexOf`, `Contains`, `ToInt()` | ① | 출력: `{a}` → `[1, 2, 3]` | |

① 함수 매크로가 필요합니다(1.20.2+). ② 인덱스가 런타임 값일 때만 1.20.2+가 필요합니다(상수 인덱스는 모든 버전에서 동작).

- `NString`은 값입니다: 대입하면 문자열이 복사됩니다. `NArray`는 객체처럼 참조입니다(`b = a`는 같은 배열이고, 메서드에 넘기면 메서드가 바꿀 수 있습니다).
- 문자열 이어 붙이기는 매크로를 거치므로 `"`나 `\`가 들어간 문자열은 이어 붙일 수 없습니다.
- 범위를 벗어나 읽으면 0이 되거나 결과가 바뀌지 않습니다. 마인크래프트는 예외를 던질 수 없습니다.
- `NArray<Milli>`는 ×1000 정수를 그대로 저장하므로(정확함), 출력하면 그 정수가 보입니다.

## 배열

`int[]`, `bool[]`, `Milli[]`, `Q16[]`는 쓰기 가능한 배열이며 정적 필드나 지역 변수로 쓸 수 있습니다. 길이는 컴파일 타임에 정해집니다.

```csharp
static int[] grid = new int[20];          // 0으로 채움
static int[] primes = { 2, 3, 5, 7 };

public static void Main()
{
    for (int i = 0; i < grid.Length; i++) grid[i] = i * i;
    grid[k] += 100;
    grid[k]++;

    int[] a = { 1, 2, 3 };                // 지역 배열: 원소에 런타임 값 가능
    int[] b = a;                          // 같은 배열 (C#처럼 참조)
    b[0] = 9;                             // a[0]도 9

    foreach (int p in primes) { if (p > 5) break; total += p; }
    m.Console.WriteLine($"{a} {string.Join("-", primes)}");   // [9, 2, 3] 2-3-5-7
}
```

- **저장 방식:** 원소 하나가 스코어 하나(`$static.grid[3]`, `$main.a[0]`)라서 `/scoreboard players get`으로 바로 볼 수 있습니다.
- **상수 인덱스**(`grid[3]` 또는 상수로 계산되는 식): 스코어에 바로 접근하므로 추가 비용이 없습니다. 범위를 벗어나면 컴파일 에러입니다.
- **런타임 인덱스**(`grid[i]`): `execute if score … matches` 함수의 이진 트리로 찾습니다 (명령어 약 log₂(n)+8개. 매크로가 필요 없어 1.20.1에서도 동작). 범위를 벗어나면 읽기는 `0`, 쓰기는 무시됩니다.
- **지원:** `a[i]`, `=`, `+= -= *= /= %=`, `++`/`--`, `a.Length`, `foreach`(`break`/`continue` 포함), 출력(`{a}` → `[1, 2, 3]`, `string.Join(sep, a)`).
- **정적 배열**은 다른 정적 필드처럼 한 번만 초기화되고 `/reload` 후에도 값이 유지됩니다. 원소는 상수여야 합니다. **지역 배열**은 선언문이 실행될 때마다 새로 만들어집니다.
- **길이:** 컴파일 타임 상수, 최대 16384. 런타임 값 `n`으로 `new int[n]`은 지원하지 않습니다.
- **배열 변수**는 컴파일 타임에 결정되므로 지역 배열 재대입(`a = b;`, `a = new int[3];`)을 분기 안에서 하면 안 됩니다. 정적 배열 필드는 재대입할 수 없습니다.
- **매개변수:** 배열 매개변수는 `[Inline]` 메서드에서만 가능합니다 (`[Inline] static int Sum(int[] xs)`). 배열을 반환할 수는 없고, 다차원·가변 배열은 지원하지 않습니다.
- `static readonly` 배열은 그대로 컴파일 타임 **표**입니다 (아래). 문자열을 포함한 상수 데이터에 쓰세요.

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

## 나눗셈과 나머지

`/`와 `%`는 C#과 같습니다. 몫은 0 방향으로 버리고, 나머지는 피제수의 부호를 따릅니다.

```csharp
int a = -7;
m.Console.WriteLine($"{a / 2} {a % 2}");                          // -3 -1  (C#과 같음)
m.Console.WriteLine($"{Math.FloorDiv(a, 2)} {Math.FloorMod(a, 2)}"); // -4 1  (use MathLib;)
```

- 스코어보드의 `/=`, `%=`는 음의 무한대 방향으로 내림하기 때문에 컴파일러가 부호 처리를 덧붙입니다. 상수로 나누면 명령어 4개, 런타임 값으로 나누면 공용 헬퍼(`<ns>:__math/idiv` / `irem`)를 호출합니다.
- `Math.FloorDiv` / `Math.FloorMod`(MathLib)는 명령어 하나짜리 내림 버전입니다. 값이 음수가 아님을 알고 있는 자주 도는 코드나, 내림이 필요할 때 쓰세요.
- `Milli` / `Q16` 나눗셈도 (고정소수점 마지막 자리에서) 0 방향으로 버리고, `%`도 같은 방식입니다. `Milli`/`Q16`의 `(int)x` 변환도 C#처럼 0 방향으로 버립니다.
- 0으로 나누면 예외를 던질 수 없으므로 값이 바뀌지 않습니다.

### 마인크래프트 나눗셈 (더 빠름)

C# 나눗셈은 부호 처리 때문에 명령어가 더 듭니다. 내림이어도 괜찮다면 `/`와 `%`를 마인크래프트의 스코어보드 연산 하나로 컴파일하게 할 수 있습니다. 이때는 음의 무한대 방향으로 내림하고, `%`는 제수의 부호를 따릅니다(`-7 / 2 = -4`, `-7 % 2 = 1`):

```csharp
[MinecraftDivision] static int Cell(int x) => x / 16;      // 메서드 하나 (또는 클래스 전체)
[CSharpDivision] static int Exact(int x) => x / 16;        // 다시 C# 방식으로
```

프로젝트 전체에 적용하려면 `mcs.json`에 `"division": "minecraft"`를 넣습니다(또는 `--division minecraft`). `Q16 / Q16`은 어느 쪽이든 정확한 헬퍼를 그대로 씁니다.

## 재귀

메서드는 직접, 또는 다른 메서드를 거쳐 자기 자신을 부를 수 있습니다:

```csharp
static int Fact(int n) { if (n <= 1) return 1; return n * Fact(n - 1); }
static bool IsEven(int n) => n == 0 ? true : IsOdd(n - 1);
static bool IsOdd(int n) => n == 0 ? false : IsEven(n - 1);
```

- **방식:** 메서드의 변수는 고정된 스코어에 있으므로, 컴파일러가 호출 그래프에서 순환을 찾아 **순환 안의 호출에서만** 호출하는 쪽의 스코어(매개변수, 지역 변수, 임시값)를 호출 전에 storage 스택(`<ns>:mcs stack`)에 저장하고 호출 뒤에 되돌립니다. 다른 호출은 그대로입니다.
- **비용:** 재귀 호출마다 그 메서드의 스코어 하나당 명령어 약 2개(저장·복원). 모두 한 틱 안에 실행되므로 깊은 재귀는 `maxCommandChainLength`(기본 65536개)에 걸릴 수 있습니다. 입력이 크면 반복문을 쓰세요.
- 재귀 메서드에서는 `Wait()`를 쓸 수 없습니다. 엔티티 매개변수(태그)는 저장되지 않으니 재귀 호출 뒤에는 엔티티 매개변수에 기대지 마세요.

## Wait()

`Wait()`는 현재 메서드를 1틱 멈추고, `Wait(n)`은 `n`틱 멈춥니다. 메서드의 나머지 부분은 `/schedule`로 나중에 실행됩니다. **기다리는 건 이 메서드뿐**이고, 호출한 쪽은 바로 계속 진행합니다.

```csharp
static void Countdown()
{
    for (int i = 3; i > 0; i--)
    {
        m.Console.WriteLine($"{i}...");
        Wait(20);                     // 1초
    }
    m.Console.WriteLine("Go!");
}

public static void Main()
{
    Countdown();                      // 첫 Wait에서 돌아옴
    m.Console.WriteLine("2...보다 먼저 출력됨");
}
```

- **쓸 수 있는 곳:** 메서드 본문의 문장, 또는 `if`/`else`, `while`, `for`, `do-while` 안 (중첩 깊이 무관). `Wait`마다 메서드가 쪼개져서 그 뒤의 코드가 continuation 함수가 됩니다.
- **중간 `return`** 은 동작합니다. `Wait()`가 들어 있는 반복문에서는 `break` / `continue`를 쓸 수 없으니 반복 조건이나 플래그를 쓰세요.
- **쓸 수 없는 곳:** `switch`, `foreach`, 람다, `[Inline]` 메서드, 엔티티 인스턴스 메서드, 값을 반환하는 메서드.
- **실행 컨텍스트:** 예약된 코드는 월드 스폰에서 서버로 실행되므로 `@s`, `Player.Self`, 상대 좌표(`~ ~ ~`)는 이어지지 않습니다. 엔티티 변수(태그), `static` 필드, 숫자 지역 변수는 이어집니다.
- **지역 변수 공유:** 지역 변수는 메서드별 스코어라서, 기다리는 중에 같은 메서드를 다시 호출하면 값이 덮어써집니다 (두 실행 모두 새 값으로 이어감).

## 오브젝트: 한 번만 사용

엔티티 오브젝트(`new Entity : Zombie x;`), 블록 오브젝트(`new Block : Stone s;`), 아이템 오브젝트(`new Item(…)`)는 월드의 **하나**를 나타냅니다. 다음 호출은 오브젝트를 **소비**합니다:

| 소비하는 것 | |
|---|---|
| `m.SpawnEntityObject(x, pos)` | 엔티티 소환 (소환 뒤에도 `x`의 속성은 바꿀 수 있음 — 이제 그 엔티티를 가리킴) |
| `m.SetBlockObject(s, pos)`, `m.FillBlockObject(s, a, b)` | 블록 설치 |
| `m.GiveObject(item, players)` | 아이템 지급 |
| `x.Dispose()` | 오브젝트 폐기 — 이후 `x`를 쓰면 에러 |

```csharp
new Entity : Pig pig;
m.SpawnEntityObject(pig, here);
m.SpawnEntityObject(pig, there);           // 에러: 'pig' was already used (line 2)
m.SpawnEntityObject(pig.Clone(), there);   // OK: 종류와 속성이 같은 새 오브젝트

if (ok) m.GiveObject(wand, p); else wand.Dispose();   // OK: 분기마다 한 번
```

- **컴파일 타임:** 같은 메서드 안에서 두 번째 소비는 에러입니다. `if`/`else`, `switch` 분기는 따로 검사합니다.
- **실행 시간:** 한 번의 소비가 여러 번 실행될 수 있는 곳 — 오브젝트를 만들지 않는 반복문 안, 또는 여러 메서드에서 쓰는 정적 오브젝트 — 에서는 플래그 스코어로 첫 실행만 통과시킵니다. 이후 시도는 아무것도 하지 않고 `[MC#] 'x' was already used …`를 출력합니다. 직선 코드에는 플래그도, 추가 명령어도 없습니다.
- **`Clone()`** 은 새 오브젝트를 만듭니다: 엔티티는 종류와 소환 전 속성, 블록은 상태, 아이템은 내용과 정체성을 유지합니다.
- 임시 오브젝트로 한 번만 쓰려면 `new …`나 `x.Clone()`을 바로 넘기면 됩니다.

## 플레이어 이벤트

정적 메서드에 이벤트 속성을 붙이면, 이벤트가 일어난 플레이어마다 **그 플레이어로, 그 플레이어 위치에서**(`@s`) 실행됩니다.

```csharp
use PlayerLib;

[OnFirstJoin] static void Welcome(Player p) { p.Give("minecraft:bread", 5); }
[OnJoin]      static void Hello(Player p)   { p.SendMessage("Welcome back!"); }
[OnDeath]     static void Died()            { deaths++; }
[OnRespawn]   static void Back(Player p)    { p.Effect("minecraft:regeneration", 5); }
[OnKill]      static void Kills(Player p, int count) { p.SendMessage($"+{count} kills"); }
[OnRightClick("carrot_on_a_stick")] static void Wand(Player p) { asm summon minecraft:lightning_bolt; }
[OnTrigger("menu")] static void Menu(Player p, int choice) { /* /trigger menu set 2 */ }
[OnStat("minecraft.custom:minecraft.jump")] static void Jumped(int times) { }
```

| 속성 | 시점 | `int` 매개변수 |
|---|---|---|
| `[OnJoin]` | 플레이어 접속 (처음 포함 매번) | — |
| `[OnFirstJoin]` | 플레이어 첫 접속 | — |
| `[OnDeath]` | 플레이어 사망 (사망 위치에서, 사망 화면 상태로 실행) | 사망 횟수 |
| `[OnRespawn]` | 사망 후 부활 | — |
| `[OnKill]` | 몹이나 플레이어를 처치 | 처치 수 |
| `[OnRightClick(item)]` | 아이템 사용 (기본 `carrot_on_a_stick`) | 사용 횟수 |
| `[OnTrigger("name")]` | `/trigger name` 실행 — **OP 권한 불필요** | 값 (그냥 `/trigger`면 `1`) |
| `[OnStat("criterion")]` | 누적 통계 증가. 예: `minecraft.mined:minecraft.stone` | 증가량 |
| `[OnRightClickObject(nameof(wand))]` | 이 [아이템 오브젝트](libraries.md#itemlib)(또는 복사본)를 들고 우클릭 | 사용 횟수 |

- **매개변수**는 선택입니다: `Player`(해당 플레이어)와 `int`. 한 이벤트에 메서드를 여러 개 달 수 있고, 소스 순서대로 실행됩니다.
- **동작 방식:** 틱 함수 하나(`<ns>:__events/tick`)가 이벤트 objective(`<ns>.ev.*`, load 시 생성)를 확인해 `execute as @a[scores={…=1..}] at @s`로 핸들러를 실행하고 카운터를 초기화합니다. 이벤트는 같은 틱 안에 감지됩니다.
- **`[OnRightClick(item)]`**의 우클릭 감지는 `carrot_on_a_stick`, `warped_fungus_on_a_stick`에서만 됩니다. 다른 아이템은 실제로 사용될 때(먹기, 던지기, 설치 등) 셉니다. 아이템 ID는 버전별로 검사합니다. 아무 아이템이나 우클릭을 감지하려면 [아이템 오브젝트](libraries.md#itemlib)와 `[OnRightClickObject]`를 쓰세요 (1.20.5+).
- **접속:** 데이터팩을 처음 설치할 때 접속해 있던 플레이어도 첫 접속으로 칩니다. "나감" 이벤트는 없습니다 (나간 플레이어로는 아무것도 실행할 수 없음).
- **부활**은 `@e`가 살아 있는 엔티티만 고른다는 점을 이용하므로 `doImmediateRespawn`에서도 동작합니다.
- **트리거:** 매 틱 모든 플레이어에게 다시 허용됩니다. objective 이름이 플레이어가 입력하는 이름이므로 겹치지 않게 정하세요.

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

## 다른 데이터팩 호출

MC#의 출력은 다른 팩이 전혀 필요 없지만, 원하면 다른 팩을 호출할 수 있습니다. 그러면 여러분의 팩이 그 팩에 의존하게 됩니다(컴파일러가 아니라 여러분의 선택입니다).

```csharp
[External("other:api/ping")] static extern void Ping();                 // function other:api/ping
[External("other:api/add")] static extern int Add(int a, int b);        // function other:api/add {a:…, b:…}
[External("#other:hooks")] static extern void Hooks();                  // 함수 태그

[McsPack("lib")]                       // MC#으로 컴파일된 팩 (네임스페이스 lib)
static class Lib {
    public static extern int Square(int x);   // $square.x mcs.lib 설정 → lib:square 실행 → $square#ret mcs.lib 읽기
}

int s = Add(2, 3) + Lib.Square(4);
m.Call("other:setup");                 // 이름으로 아무 함수나 #태그 실행
```

- `[External]`의 매개변수는 매크로 인자(`$(a)`, 1.20.2+)가 됩니다: `int`, `bool`, `Milli`, `Q16`, `string`, `NString`. `int` / `bool` 결과는 그 함수의 `/return` 값입니다.
- `[McsPack("ns", "Class")]`는 다른 팩의 `Class` 클래스에 선언된 메서드를 부릅니다(`ns:class/method`). 매개변수와 결과는 그 팩의 스코어를 쓰므로 `int`, `bool`, `Milli`, `Q16`이어야 합니다.
- 다른 팩이 먼저 로드되어 있어야 합니다(그 팩의 objective는 그 팩의 load 함수가 실행된 뒤에 생깁니다).

## 리소스

함수가 아닌 파일(월드 프리셋, 블록 태그 등)은 폴더 하나에 모아 두고 `mcs.json`의 `"resources"`에 지정하세요. 데이터팩에 그대로 복사됩니다.

```json
{ "namespace": "war", "sources": ["src"], "resources": "res", "minecraft": "1.21.9-26.3" }
```
