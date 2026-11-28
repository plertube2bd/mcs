# 2. 변수와 수명

MC#의 숫자 변수는 전부 **스코어보드 스코어**입니다. 그래서 "변수가 언제 만들어지고 언제 사라지는가"가 일반 C#과 다릅니다. 이 장에서는 그 규칙을 정확히 익힙니다.

## 세 종류의 변수

| 종류 | 예 | 저장 위치 | 값이 유지되는 기간 |
|---|---|---|---|
| 지역 변수 | `int local = 0;` | `$<메서드>.<이름>` | 선언이 실행될 때마다 새로 초기화 |
| 정적 필드 | `static int loads = 0;` | `$static.<이름>` | **월드가 존재하는 동안 계속.** `/reload`와 서버 재시작 뒤에도 유지 |
| 상수 | `const int Max = 10;` | 저장되지 않음 | 컴파일할 때 값으로 바뀜 |

## static 초기값은 `/reload` 때 다시 들어가지 않습니다

[`examples/t02_lifetime.mcs`](examples/t02_lifetime.mcs):

```csharp
// @do 1 reload
// @do 2 reload

static int loads = 0;              // 월드에 처음 설치될 때 한 번만 0이 들어간다
static int energy;                 // [Load]에서 매번 다시 넣는다

[Load] static void Init()
{
    energy = 100;
}

public static void Main()
{
    loads++;
    energy -= 10;
    int local = 0;                 // 지역 변수: 선언이 실행될 때마다 0
    local++;
    m.Console.WriteLine($"loads={loads} energy={energy} local={local}");
}
```

`// @do 1 reload`는 "1번째 틱에 `/reload`를 실행하라"는 시뮬레이터 지시입니다. 직접 실행할 때는 `mcs run t02_lifetime.mcs --do "1 reload" --do "2 reload"`처럼 씁니다.

결과:

```
[t0] loads=1 energy=90 local=1
[t1] * reload
[t1] ! An objective already exists by the name mcs.t02_lifetime  ← scoreboard objectives add mcs.t02_lifetime dummy
[t1] loads=2 energy=90 local=1
[t2] * reload
[t2] ! An objective already exists by the name mcs.t02_lifetime  ← scoreboard objectives add mcs.t02_lifetime dummy
[t2] loads=3 energy=90 local=1
```

읽는 법:

- `loads`는 1 → 2 → 3으로 계속 늘어납니다. 초기값 `= 0`은 **스코어가 아직 없을 때 한 번만** 들어갑니다.
- `energy`는 매번 90입니다. `[Load]` 메서드가 로드할 때마다 100을 넣기 때문입니다.
- `local`은 매번 1입니다. 지역 변수는 선언이 실행될 때마다 초기화됩니다.
- `Main()`과 `[Load]`는 `/reload`할 때마다 다시 실행됩니다.
- `!` 줄은 Minecraft가 이미 있는 objective를 다시 만들려는 명령을 거부했다는 표시입니다. 게임에서도 똑같이 조용히 실패하며, 해롭지 않습니다.

### 꼭 기억할 것: 소스의 초기값을 바꿔도 기존 월드에는 반영되지 않습니다

`static int maxHp = 20;`을 `= 40;`으로 고치고 `/reload`해도, 이미 그 월드에 `maxHp` 스코어가 있으면 20이 남습니다. 규칙은 이렇습니다.

| 이런 값이라면 | 이렇게 씁니다 |
|---|---|
| 게임 진행에 따라 바뀌고, 저장되어야 하는 값 (점수, 진행도) | `static int score = 0;` |
| 설정값처럼 로드할 때마다 소스 값으로 맞춰야 하는 값 | `[Load]`에서 대입하거나, `const`로 선언 |
| 절대 바뀌지 않는 값 | `const int MaxHp = 40;` |

월드의 MC# 상태를 완전히 처음으로 되돌리려면 게임에서 다음을 실행합니다.

```
/scoreboard objectives remove mcs.<네임스페이스>
/data remove storage <네임스페이스>:mcs
/reload
```

## 정적 변수의 이름을 묶기

최상위에 쓴 `static` 필드는 모든 파일이 **한 공간**을 씁니다. 두 파일에 `static bool lit;`이 있으면 다음 오류가 납니다 (조용히 합쳐지지는 않습니다).

```
f2.mcs(1,13): error MCS: CS0102: The type '__Global' already contains a definition for 'lit'
```

기능이 늘어나면 정적 클래스로 묶습니다. 클래스마다 스코어 이름이 따로 생기므로, 같은 필드 이름을 써도 겹치지 않습니다.

[`examples/t02_names.mcs`](examples/t02_names.mcs):

```csharp
static class Lights
{
    public static bool lit = true;
    public static int level = 15;
}

static class Clock
{
    public static bool isNight = false;
    public static int level = 3;   // Lights.level과 다른 스코어
}

public static void Main()
{
    Clock.isNight = true;
    Lights.lit = !Clock.isNight;
    m.Console.WriteLine($"lit={Lights.lit} night={Clock.isNight} {Lights.level} {Clock.level}");
}
```

```
[t0] lit=False night=True 15 3
```

생기는 스코어는 `$static.lights.level`, `$static.clock.level`입니다. 클래스 안에 클래스를 넣어도(`Lamps.State.lit`) 경로 전체가 이름에 들어갑니다(`$static.lamps.state.lit`). 그래서 서로 다른 필드가 같은 스코어를 쓰는 일은 없습니다.

> **권장 구조:** 기능 하나에 파일 하나, 파일 하나에 정적 클래스 하나. `Lights.cs`의 상태는 `Lights.*`, `Clock.cs`의 상태는 `Clock.*`.

## 게임 안에서 값 보기

정적 필드의 스코어 이름은 바뀌지 않으므로 게임 안에서 바로 읽을 수 있습니다.

```
/scoreboard players get $static.lights.level mcs.<네임스페이스>
```

## 정리

- 정적 필드는 월드에 저장됩니다. 초기값은 처음 한 번만 들어갑니다.
- 로드할 때마다 값을 맞추려면 `[Load]`에서 대입합니다.
- 이름 충돌은 정적 클래스로 막습니다. 최상위끼리의 충돌은 컴파일 오류로 알려 줍니다.
- 관련 명세: [9.2 이름](../spec.md#92-이름), [9.3 수명](../spec.md#93-수명), [10.1 로드 순서](../spec.md#101-로드-순서)

다음: [3. 실행 순서](03-order.md)
