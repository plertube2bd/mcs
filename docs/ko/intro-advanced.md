# Minecraft# — 기술 개요

> 데이터팩·C#·컴파일러에 익숙한 사람을 위한 소개입니다. 처음이라면 [일반 사용자용 소개](intro.md)를 먼저 보세요.

## 개요

MC#은 **C# 문법의 부분집합**을 `.mcfunction` 데이터팩으로 컴파일하는 DSL입니다.

| 항목 | 내용 |
|---|---|
| 프런트엔드 | Roslyn. 파싱, 타입 검사, 상수 폴딩을 C# 컴파일러가 그대로 수행 |
| 확장 문법 | `use`, `new T : K x;`, `asm` — 전처리기가 일반 C#으로 내림 (`--emit-csharp`로 확인) |
| 백엔드 | Roslyn 구문 트리/의미 모델 → mcfunction |
| 대상 | Minecraft Java **1.20.1 – 26.3**, 버전별 분기 + 오버레이로 단일 데이터팩 출력 |
| 런타임 의존성 | 없음. 바닐라 명령어만 출력 |
| 빌드 환경 | .NET 8 SDK |
| 라이선스 | MIT |

## 동기

모드는 런타임 능력(JVM, 라이브러리, 임의 연산)이 압도적이지만, 클라이언트·서버 모두에 로더가 필요하고 맵 단위 배포가 어렵습니다. 데이터팩은 배포가 쉽지만 언어로서는 어셈블리 수준입니다. 변수는 스코어보드, 지역성은 태그, 제어 흐름은 함수 분할과 `execute if`로 직접 만들어야 합니다.

MC#은 이 하부 구조를 컴파일러로 감춥니다. 배포 모델은 데이터팩 그대로, 작성 모델은 정적 타입 언어로 가져갑니다.

## 컴파일 예

```csharp
use EntityLib;

public static void Main() {
    new Entity : Zombie z;              // Entity z = new Entity(EntityType.Zombie);
    z.maxHealth = 100f;                 // 소환 전 설정 → summon 직후 적용
    z.Health = 100f;
    if (!m.SpawnEntityObject(z, GetWorldSpawn())) return;
    m.Console.WriteLine($"Spawned {z} with health {z.Health}");
}
```

출력 (1.21.4, 발췌):

```mcfunction
# main.mcfunction
scoreboard players set $main#ctl mcs.zombie 0
scoreboard players set $main#t0 mcs.zombie 0
tag @e[tag=mcs.zombie.main.z] remove mcs.zombie.main.z
execute store success score $main#t0 mcs.zombie at @e[type=minecraft:marker,tag=mcs.zombie.spawn,limit=1] run summon minecraft:zombie ~ ~ ~ {Tags:["mcs.zombie.main.z"]}
execute if score $main#t0 mcs.zombie matches 1 run function zombie:main/_init0
execute unless score $main#t0 mcs.zombie matches 1 run scoreboard players set $main#ctl mcs.zombie 1
execute if score $main#ctl mcs.zombie matches 0 run function zombie:main/_cont1
```

볼 점:
- **조기 `return`** 은 `$main#ctl` 플래그와 continuation 함수(`_cont1`)로 내려갑니다.
- **엔티티 변수** `z`는 `mcs.<ns>.<메서드>.<변수>` 태그로 표현되고, 재할당 전에 이전 태그를 정리합니다.
- **`float` 보간**은 ×1000 고정소수점 스코어를 부호·정수부·소수 3자리로 분해해 `tellraw` 컴포넌트로 출력합니다 (`_cont1`, 16줄).
- 소스 8줄 → 함수 3개, 25줄.

## 타입 매핑

| MC# | 표현 |
|---|---|
| `int`, `bool` | 스코어보드 (`mcs.<ns>` objective) |
| `Milli` (구 `float`) | 스코어보드, ×1000 고정소수점 |
| `Q16` (구 `double`) | 스코어보드, Q15.16 고정소수점 (×65536) |
| `string` | **컴파일 타임 상수만**. 런타임 문자열은 `NString` (NBT 스토리지) |
| 클래스, 구조체, 제네릭 | 필드를 스코어로 펼침. 메서드는 함수, 제네릭은 타입 인자별로 컴파일 |
| `Entity`, 엔티티 클래스 | 태그 (`@e[tag=…,limit=1]`) |
| `Position` | execute 접두사 (`at …` / `positioned …`) |
| `int[]` 등 배열 | 원소마다 스코어 하나. 런타임 인덱스는 이진 탐색 함수 트리 |
| `static readonly` 배열 | 컴파일 타임 표. 런타임 인덱스는 스토리지 + 매크로 (1.20.2+) |
| `static` 필드 | `$static.<클래스 경로>.<필드>` 스코어. 처음 한 번만 초기화되고 리로드 후에도 유지 |

스코어보드 나눗셈은 음의 무한대 방향으로 내림하지만, 컴파일러가 부호 처리를 덧붙여 `/`와 `%`는 C#과 같은 결과를 냅니다. 내림 버전은 `Math.FloorDiv`/`FloorMod`입니다.

## 언어 기능

- **제어 흐름:** `if/else`, `while`, `for`, `do-while`, `switch`(정수·열거형), `break`/`continue`, 값 반환, `?:`, 단락 평가 `&&`/`||`
- **엔티티 클래스:** `[Entity("minecraft:marker", "tag")] partial class P : EntityObject`. 필드 하나가 objective 하나(`[Score]`)이고, 인스턴스 메서드는 `execute as <p> at @s run function`으로 내려갑니다.
- **엔티티 질의:** `Entities.All<T>(filter)`, `Nearest`, `Random`, `Count`, `Any`, `Spawn`. 엔티티 `foreach`는 각 엔티티 컨텍스트에서 본문을 실행하고, 중첩 시 필요한 만큼 자동 태깅합니다.
- **컴파일 타임 메타프로그래밍:** `[Inline]`(상수 인자 전파, 상수 분기 제거), `Unroll.Range`, 배열 `foreach` 전개. 외부 코드 생성기가 필요 없습니다.
- **인라인 명령어:** `asm`, `asm $"tp @s {x} 100 {z}"`. 상수는 치환하고, 런타임 값은 함수 매크로로 넣습니다.
- **명령어 결과 바인딩:** `m.Check(cond)`, `m.Result(cmd)`, `m.Success(cmd)`, `Exec.Run(subcmd, lambda)`, `m.Schedule`
- **스코어 연결:** `m.Mount(hello, "test.hello")`로 필드를 기존 스코어에, `class test : dummy {}` + `m.Mount(test, "test")`로 클래스를 objective에 연결합니다 (컴파일 타임 바인딩).
- **플레이어 이벤트:** `[OnJoin]`, `[OnFirstJoin]`, `[OnDeath]`, `[OnRespawn]`, `[OnKill]`, `[OnRightClick]`, `[OnTrigger]`, `[OnStat]`. 이벤트 objective를 틱마다 확인해 해당 플레이어로 실행합니다.
- **오브젝트:** 엔티티·블록·아이템 오브젝트는 한 번만 소환·설치·지급할 수 있습니다 (컴파일 타임 + 반복문은 실행 시간 검사, `Clone()` / `Dispose()`). 아이템 오브젝트는 `[OnRightClickObject]`로 우클릭을 감지합니다.
- **틱 대기:** `Wait()` / `Wait(n)`. 메서드를 continuation 함수로 쪼개 `/schedule`로 이어 실행합니다 (if/while/for/do-while 안에서도 동작).
- **진입점:** `Main()`(load 시 실행), `[Load]`, `[Tick]`. 모든 메서드는 `/function <ns>:<snake_case>`로도 호출할 수 있습니다.

## 버전 대응

최소 공통 기능만 쓰는 방식이 아닙니다. 대상 범위 안의 **팩 포맷 그룹마다 따로 컴파일**하고, 결과가 같은 그룹은 합친 뒤 `pack.mcmeta` 오버레이로 묶습니다.

```
$ ./mcs build
Build succeeded: out/versions (Minecraft 1.20.1 – 26.3)
  base           1.20.1             3 functions
  mcs_1_20_2     1.20.2-1.20.6      4 functions
  mcs_1_21       1.21-1.21.1        4 functions
  mcs_1_21_2     1.21.2-1.21.8      4 functions
  mcs_1_21_9     1.21.9-26.3        6 functions
```

컴파일러가 흡수하는 차이: `functions/` → `function/`, 속성 ID의 `generic.` 제거, `boat` → `oak_boat`, `min_format`/`max_format`, 1.21.9+의 스폰 청크 제거(forceload 후 `Main` 지연), 매크로 유무.

코드 안에서는 `MC_<버전>_OR_GREATER` 심볼과 `#if`로 분기합니다 (.NET의 `NET6_0_OR_GREATER`와 같은 방식). 일부 버전에서 표현할 수 없는 기능을 쓰면 해당 버전을 붙여 오류를 냅니다:

```
macro.mcs(5,19): error MCS: [1.20.1] Setting maxHealth from a runtime value needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or raise the minimum target version.
```

블록·아이템·엔티티·효과 ID는 대상 버전별 데이터(`Data/mcdata.json`)로 검증합니다.

## 정적 검사 (게임 없이 디버깅)

- `CSxxxx` 오류: Roslyn이 보고한 타입·문법 오류
- `MCS` 오류: 데이터팩으로 표현할 수 없는 구성, 또는 특정 버전에 없는 ID·기능
- 결정적인 이름 규칙(`$<메서드>.<변수>`, `$<메서드>#t0`, `<ns>:mcs` 스토리지) 덕분에, 런타임 상태는 `/scoreboard players get $main.sum mcs.hello`처럼 바로 조회할 수 있습니다.

런타임 동작은 `mcs run`으로 확인합니다. 내장 시뮬레이터가 스코어(32비트 넘침, 나눗셈 규칙), 스토리지, 매크로, 함수 호출 순서, `/schedule`, `/reload`, 대본 플레이어를 게임과 같게 계산합니다. 블록·위치·엔티티 NBT는 계산하지 않고 실행된 명령만 기록합니다. `tests/run.py --real`은 같은 팩을 실제 서버에서 돌려 시뮬레이터와 비교합니다.

## 제약

| 제약 | 이유 |
|---|---|
| 재귀는 storage 스택으로 동작 (호출마다 변수 저장·복원 비용) | 지역 변수가 함수별 정적 스코어 |
| 객체 참조는 컴파일 타임에 고정 (`null`, 조건부 재대입 없음) | 객체가 고정된 스코어 묶음 |
| `T[]` 길이는 컴파일 타임 상수 (가변 길이는 `NArray<T>`) | 원소마다 스코어를 미리 할당 |
| 런타임 값 속성 설정은 1.20.2+ | 함수 매크로 필요 |
| `Milli` 소수 3자리, `Q16` 1/65536 (범위 ±32768) | 고정소수점 스코어 표현 |
| `Wait()` 뒤에는 `@s`·위치가 이어지지 않음 | `/schedule`은 서버·월드 스폰에서 실행 |
| `GetWorldSpawn()`은 load 시점 마커 기준 | `/setworldspawn` 후 `/reload` 필요 |

## 구조와 확장

| 파일 | 역할 |
|---|---|
| `Preprocessor.cs` | MC# 확장 문법 → C# |
| `Prelude.cs` | 내장 라이브러리의 C# 선언 |
| `Codegen.cs`, `Codegen.*.cs` | 구문 트리 → mcfunction |
| `Versions.cs` | 버전 표와 `TargetProfile` (버전 분기는 전부 여기) |
| `McData.cs`, `Data/mcdata.json` | 버전별 레지스트리 데이터, 블록 클래스 생성 |
| `Datapack.cs` | 분기 병합, 오버레이, `pack.mcmeta`, 출력 |

새 마인크래프트 버전은 `Versions.Releases`에 한 줄을 추가하면 지원됩니다. 동작이 바뀌었다면 `TargetProfile`에 속성을 추가합니다.

## 참고

- [언어 명세](spec.md) · [튜토리얼](tutorial/README.md) · [API 레퍼런스](../api.md)
- [컴파일하기](compiling.md) · [라이브러리 레퍼런스](libraries.md) · [언어 가이드](language.md)
- https://github.com/plertube2bd/mcs
