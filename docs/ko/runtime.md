# MC# 런타임 (mcs.jar)

MC# 데이터팩을 위한 서버 모드/플러그인입니다. **Fabric, NeoForge, Paper**용이 있고, 각 jar 하나가 **Minecraft 26.1 – 26.3**에서 동작합니다. 데이터팩은 이 jar 없이도 동작하며, 설치하면 다음 기능이 생깁니다.

1. **빠른 경로**: 일부 연산을 명령어 수십~수백 개 대신 한 단계로 계산합니다. 결과는 바닐라와 비트 단위로 같습니다.
2. **권한 3·4 명령어**: `Runtime.Sudo(...)`로 데이터팩 함수에서 `ban`, `op`, `whitelist`, `stop` 같은 명령을 실행합니다.
3. **함수 숏컷**: `/<네임스페이스>:<함수>`로 함수를 바로 실행합니다. `__`로 시작하는 경로는 숨깁니다.
4. **런타임 디버깅**: MC# 변수 이름으로 값 보기·바꾸기·감시, 함수 호출 추적, 프로파일
5. **인터프리터**: `/mcsi`로 MC# 코드를 대화형으로 실행합니다(OP 전용). 실행 중인 데이터팩의 변수와 메서드에 연결됩니다.

## 설치

`runtime/build.sh`로 빌드합니다(Java 25, Gradle 9 필요). 결과는 `runtime/dist/`에 생깁니다.

| 서버 | 파일 | 넣을 곳 | 필요한 것 |
|---|---|---|---|
| Fabric | `mcs-fabric.jar` | `mods/` | Fabric API |
| NeoForge | `mcs-neoforge.jar` | `mods/` | — |
| Paper | `mcs-paper.jar` | `plugins/` | — |

설정 파일은 처음 시작할 때 만들어집니다. Fabric·NeoForge는 `config/mcs-runtime.properties`, Paper는 `plugins/MCSRuntime/mcs-runtime.properties`입니다.

| 키 | 설명 | 기본값 |
|---|---|---|
| `sudo.enabled` | 데이터팩 함수의 권한 3·4 명령 허용 | `true` |
| `sudo.log` | sudo 명령을 서버 로그에 기록 | `true` |
| `shortcuts.enabled` | `/<ns>:<함수>` 숏컷 | `true` |
| `interpreter.permission` | `/mcsi`에 필요한 권한 레벨 | `2` |
| `debug.permission` | 디버거(`/mcs vars`, `watch`, `trace`, `profile` …)에 필요한 권한 레벨 | `2` |
| `interpreter.max-steps` | 인터프리터 입력 하나당 최대 반복 횟수 | `100000` |

## 데이터팩 쪽: 의존성 선언

`mcs.json`의 `"runtime"`(또는 `--runtime`)으로 정합니다.

| 값 | jar가 있을 때 | jar가 없을 때 |
|---|---|---|
| `optional` (기본) | 빠른 경로 사용 | 원래 명령어로 동작. 로드할 때 경고를 한 번 출력합니다: `[mcsRuntimeServices] Warn: There is not MC# Runtime Server Mod. Performance may be reduced, but there are no issues with the datapack.` (빠른 경로를 쓰는 팩만) |
| `required` | 빠른 경로 + `Runtime.Sudo` | **팩이 시작되지 않습니다.** 로드할 때 오류 메시지를 출력하고, `Main`/`[Load]`를 실행하지 않으며, `[Tick]` 메서드는 바로 반환합니다. |
| `off` | 사용하지 않음 | — |

```json
{ "namespace": "game", "minecraft": "26.1-26.3", "runtime": "required" }
```

- jar는 26.1 이상에만 있으므로, 빠른 경로와 감지 코드는 26.1 이상 레이어에만 들어갑니다. `required`는 대상 범위가 26.1 이상이어야 합니다.
- jar 전용 명령(`mcs …`)은 모두 매크로 줄에 들어갑니다. 매크로 줄은 호출될 때만 파싱되므로, jar가 없는 서버에서도 함수 로드 오류가 나지 않습니다. 로드할 때 `<ns>:__rt/probe`가 jar의 프로토콜 버전을 묻고 결과를 `$__rt`에 둡니다.

### MC# API

```csharp
if (Runtime.IsAvailable) m.Console.WriteLine("mcs.jar 있음");   // 26.1 미만이나 "off"에서는 항상 false
Runtime.Sudo("whitelist on");                                   // "runtime": "required" 필요
Runtime.Sudo($"ban {name} 규칙 위반");                           // 실행 중 값도 가능 (매크로)
```

`Runtime.Sudo`는 권한 레벨 4로, 함수 안의 그 위치에서 바로 실행됩니다. **함수 파일 안에서만** 쓸 수 있고, 플레이어·콘솔·커맨드 블록은 `mcs sudo`를 쓸 수 없습니다. jar는 함수가 파싱되는 컴파일 컨텍스트에서만 이 명령을 허용합니다. 단, 권한 2로 `/function`을 실행할 수 있는 사람은 그 함수 안의 sudo도 실행시킬 수 있습니다. 신뢰하는 데이터팩에만 쓰세요.

### 빠른 경로가 적용되는 연산

| 연산 | 26.1–26.2 | 26.3 |
|---|---|---|
| `Q16` `*` `/`, `Milli` `*` `/`, C# `int` `/` `%` | 빠른 경로 | `/compute`가 이미 한 명령이라 대부분 그대로 |
| `Math.Sin` / `Cos` / `Tan` | 빠른 경로 (표 + 보간과 같은 결과) | `/compute` |
| `NString.IndexOf` / `Contains` | 빠른 경로 | 빠른 경로 |

헬퍼 함수(`<ns>:__math/*`, `<ns>:__str/indexof`)의 첫 줄이 jar로 넘기고, jar는 그 헬퍼를 명령 하나하나 그대로 따라 계산합니다(스코어보드 `/`는 내림 나눗셈, 0으로 나누면 값 유지, 정수 오버플로 포함). `runtime/test/realtest.py`가 jar가 있을 때와 없을 때의 결과가 같은지 실제 서버에서 확인합니다.

## 함수 숏컷

```
/game:start                 # = /function game:start
/game:give {item:"apple"}   # 매크로 인자
```

- `function`과 같은 권한(레벨 2)이 필요하고, 같은 순서로 실행됩니다(함수 안에서 써도 됩니다).
- 숨기는 것: 경로에 `__`로 시작하는 부분이 있는 함수, `minecraft:` 함수, MC# 팩에서 컴파일러가 만든 하위 함수(`main/_fn1`, `_wait0` …)
- 기존 명령과 이름이 겹치면 만들지 않습니다. `/reload`하면 다시 만듭니다.

## 디버거 (`/mcs`)

MC# 컴파일러가 `data/<ns>/mcs/symbols.json`에 변수와 메서드의 위치(스코어 홀더, 스토리지 경로, 함수 경로)를 적어 둡니다. 디버거와 인터프리터는 이 파일로 MC# 이름을 찾습니다.

| 명령 | 설명 |
|---|---|
| `/mcs version` | jar 버전, 로더, 프로토콜 |
| `/mcs packs` | 로드된 MC# 데이터팩 |
| `/mcs vars <팩> [메서드]` | 정적 변수(또는 그 메서드의 매개변수·지역 변수)와 값 |
| `/mcs get <팩> <이름>` / `/mcs set <팩> <이름> <값>` | 값 하나 읽기 / 쓰기. 이름은 `count`, `Cls.field`, `Main.n`(지역 변수) |
| `/mcs watch <팩> <이름>` | 값이 바뀔 때마다 알림 (`/mcs watch`만 쓰면 목록) |
| `/mcs unwatch [<팩> <이름>]` | 감시 중지 |
| `/mcs trace start [팩]` / `stop` | 함수 호출을 깊이별로 표시 (최대 300줄) |
| `/mcs profile start [팩]` / `stop` / `show` | 함수별 호출 수, 명령 수, 자체 시간(ms) |

추적과 프로파일은 켜져 있는 동안에만 함수를 감싸고, 끄면 원래 함수로 돌려놓습니다. 평소에는 속도에 영향이 없습니다.

## 인터프리터 (`/mcsi`)

```
/mcsi                       # 대화형 모드: 채팅이 MC# 코드가 됩니다. "exit"로 나가기
/mcsi run <코드>            # 한 번 실행 (콘솔에서도 가능)
/mcsi use <팩>              # 연결할 데이터팩 (로드된 MC# 팩이 하나면 자동)
/mcsi vars | reset | exit
```

```csharp
int x = 6 * 7;
print($"x={x}");               // m.Console.WriteLine도 가능
count += 10;                   // 연결된 팩의 정적 변수 (스코어에 바로 씁니다)
print(Add(2, 3));              // 팩의 메서드 호출 (매개변수 스코어 → 함수 실행 → 반환 스코어)
for (int i = 0; i < 3; i++) Spawn(i);
/say 명령어도 그대로          // "/"로 시작하는 줄은 마인크래프트 명령
int n = cmd("data get storage game:mcs v");   // 명령 결과
```

- **지원**: `int` `bool` `Q16` `Milli` `string` `var` 지역 변수, 연산자, `if` `while` `for` `do` `break` `continue`, 삼항 연산자, 캐스트, `$"…{식}…"`, `Math.*`, `m.Random`
- **팩과 연결**: 정적 변수(`int`/`bool`/`Milli`/`Q16`/`NString`)를 읽고 쓰고, `int`/`bool`/`Milli`/`Q16` 매개변수와 반환값을 가진 static 메서드를 호출합니다. 메서드는 실행하는 사람을 `@s`로 해서 끝까지 실행된 뒤 다음 문장으로 넘어갑니다.
- **지원하지 않음**: 클래스와 객체, `new`, `foreach`, `switch`, `Wait()`, 라이브러리 객체(`Player`, `Entity` …)는 쓸 수 없습니다. 이런 것은 `/명령어`나 팩의 메서드로 실행하세요.
- 숫자는 C#처럼 동작합니다(`int`는 32비트 오버플로, `/`·`%`는 0 방향). 팩 변수에 쓸 때는 그 타입의 정밀도로 반올림됩니다.
- 명령은 실행한 사람의 권한으로 실행됩니다. 인터프리터를 써도 권한이 올라가지 않습니다.

## 테스트

```sh
runtime/build.sh
python3 runtime/test/realtest.py                       # Fabric·NeoForge·Paper × 26.1.2·26.2·26.3
python3 runtime/test/realtest.py --loaders paper --versions 26.3
```

서버는 `~/.cache/mcs/runtime-servers`에 한 번 내려받습니다. 각 서버에 테스트 팩 두 개(`runtime/test/rtcheck.mcs`, `rtsudo.mcs`)를 넣고 콘솔로 모든 기능을 확인합니다. 시뮬레이터(`tests/run.py`)에서는 `// @runtime`을 적은 테스트가 jar가 설치된 서버처럼 실행됩니다.

## 구조

| 폴더 | 내용 |
|---|---|
| `runtime/common/` | 공통 코드 (바닐라 Minecraft 클래스만 사용). 26.1.2, 26.2, 26.3에서 같은 바이트코드로 컴파일됩니다. |
| `runtime/fabric/`, `neoforge/`, `paper/` | 로더별 연결 코드 (명령 등록, 틱, 채팅, 접속 종료) |
| `runtime/test/` | 실서버 테스트 |
