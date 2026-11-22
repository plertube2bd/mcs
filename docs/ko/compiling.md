# MC# 컴파일하기

MC# 소스 파일(`.mcs`)을 마인크래프트 데이터팩으로 컴파일하고 월드에 적용하는 방법을 설명합니다.

## 1. 준비물

| 항목 | 버전 | 비고 |
|---|---|---|
| .NET SDK | 8.0 이상 | `dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet`으로 홈 디렉터리에 설치할 수 있습니다 |
| 마인크래프트 | Java Edition **1.20.1 – 26.3** | 서버와 싱글플레이 모두 지원합니다. 그 밖의 버전은 빌드 오류입니다. |

`./mcs` 스크립트는 `~/.dotnet`을 자동으로 찾으므로 `PATH`를 바꿀 필요가 없습니다.

## 2. 소스 작성

```csharp
// hello.mcs
use EntityLib;

public static void Main() {
    new Entity : Zombie x;
    x.maxHealth = 100f;
    x.Health = 100f;
    if (!m.SpawnEntityObject(x, GetWorldSpawn())) return;
    m.Console.WriteLine($"소환 완료. UUID : {string.join("", x.uuid)}");
}
```

## 3. 컴파일

빌드 방법은 두 가지이고, 결과는 같습니다.

### a) 프로젝트 파일 (`mcs.json`)

```json
{
  "namespace": "hello",
  "sources": ["src/*.mcs"],
  "output": "out/hello",
  "minecraft": "1.20.1-26.3"
}
```

```sh
./mcs build                 # ./mcs.json 사용
./mcs build path/mcs.json   # 다른 프로젝트 파일 사용
./mcs build --mc 1.21.4     # 명령줄 옵션이 프로젝트 파일보다 우선합니다
```

| 필드 | 설명 | 기본값 |
|---|---|---|
| `namespace` | 데이터팩 네임스페이스 (`a-z 0-9 _ . -`) | 첫 번째 소스 파일 이름 |
| `sources` | 파일, 폴더(안의 `.mcs` 전부), 또는 `dir/*.mcs` 패턴. `mcs.json` 기준 상대 경로 | `*.mcs` |
| `output` | 출력 폴더. `mcs.json` 기준 상대 경로 | `out/<네임스페이스>` |
| `minecraft` | 대상 버전 또는 범위 (아래 참고) | `1.20.1-26.3` |
| `resources` | 데이터팩에 그대로 복사할 폴더 (월드 생성, 태그 등) | — |
| `description` | `pack.mcmeta` 설명 | `MC# datapack: <ns> (...)` |

### b) 명령줄

```sh
./mcs hello.mcs -o out/hello --mc 1.20.1-26.3
```

| 옵션 | 설명 | 기본값 |
|---|---|---|
| `<file.mcs ...>` | 소스 파일. 여러 개를 넘기면 하나의 데이터팩으로 묶입니다. | (필수) |
| `--mc <버전>` / `--mc <최소>-<최대>` | 대상 마인크래프트 버전 또는 범위 | `1.20.1-26.3` |
| `-o`, `--out <dir>` | 출력할 데이터팩 폴더 | `out/<네임스페이스>` |
| `-n`, `--namespace <ns>` | 데이터팩 네임스페이스 | 첫 번째 파일 이름 |
| `--versions` | 지원 버전, 팩 포맷, `#if` 심볼 목록 출력 | — |
| `--emit-csharp` | MC# 문법이 변환된 C# 코드 출력 (디버깅용) | — |

## 4. 대상 버전과 분기

빌드는 **버전별로 분기**됩니다. 컴파일러는 모든 버전이 공통으로 가진 기능(교집합)만 쓰지 않습니다. 대상 범위 안의 버전 그룹마다 코드를 한 번씩 컴파일하고, 각 그룹이 지원하는 가장 좋은 명령어를 사용합니다. 결과가 똑같은 그룹은 하나로 합쳐집니다.

```
$ ./mcs build
Build succeeded: out/versions (Minecraft 1.20.1 – 26.3)
  base           1.20.1             3 functions
  mcs_1_20_2     1.20.2-1.20.6      4 functions
  mcs_1_21       1.21-1.21.1        4 functions
  mcs_1_21_2     1.21.2-1.21.8      4 functions
  mcs_1_21_9     1.21.9-26.3        6 functions
```

- **범위(`1.20.1-26.3`)** 를 지정하면 데이터팩 하나가 만들어집니다. 각 분기는 버전별 오버레이 폴더에 들어가고, 마인크래프트가 알맞은 분기를 자동으로 고릅니다.
- **단일 버전(`1.21.4`)** 을 지정하면 그 버전 전용 데이터팩이 오버레이 없이 만들어집니다.
- **1.20.1 – 26.3 밖의 버전**(예: `1.19.4`, `26.4`)은 빌드 오류입니다.

### 컴파일러가 자동으로 분기하는 항목

| 버전 | 달라지는 점 | MC#의 처리 |
|---|---|---|
| 1.20.2+ | 함수 매크로 | 실행 중 값으로 속성 설정 가능 (`x.maxHealth = hp;`) |
| 1.21+ | `functions/` 폴더가 `function/`으로 변경 | 알맞은 폴더에 출력 |
| 1.21.2+ | 속성 ID에서 `generic.` 제거, `boat` → `oak_boat` | 알맞은 ID로 출력 |
| 1.21.9+ | `pack.mcmeta`가 `min_format`/`max_format` 사용 | 알맞은 필드로 출력 |
| 1.21.9+ | 스폰 청크 삭제 (`#load` 실행 시점에 로드된 청크가 없음) | `GetWorldSpawn()`을 쓰면 스폰 청크를 forceload하고, 로드가 끝난 뒤 `Main` 시작 |

### 코드 안에서 분기하기 (`#if`)

버전 그룹마다 `MC_<버전>_OR_GREATER` 형식의 심볼이 정의됩니다 (.NET의 `NET6_0_OR_GREATER`와 같은 방식). C#의 `#if`로 사용합니다:

```csharp
float hp = 30f;
#if MC_1_20_2_OR_GREATER
    hp = hp + 20f;
    h.maxHealth = hp;        // 실행 중 값 → 매크로 사용 (1.20.2+)
#else
    h.maxHealth = 30f;       // 1.20.1에는 매크로가 없어 상수만 가능
#endif
```

범위 안의 일부 버전에 없는 기능을 쓰면, 오류 메시지에 해당 버전이 표시됩니다:

```
macro.mcs(5,19): error MCS: [1.20.1] Setting maxHealth from a runtime value needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or raise the minimum target version.
```

해결 방법은 두 가지입니다. 코드를 `#if`로 감싸거나, 최소 버전을 올리면 됩니다 (`--mc 1.20.2-26.3`).

심볼은 **팩 포맷 그룹의 첫 버전**에만 있습니다. `./mcs --versions`로 목록을 볼 수 있습니다:

```
MC_1_20_1_OR_GREATER  MC_1_20_2_OR_GREATER  MC_1_20_3_OR_GREATER  MC_1_20_5_OR_GREATER
MC_1_21_OR_GREATER    MC_1_21_2_OR_GREATER  MC_1_21_4_OR_GREATER  MC_1_21_5_OR_GREATER
MC_1_21_6_OR_GREATER  MC_1_21_7_OR_GREATER  MC_1_21_9_OR_GREATER  MC_1_21_11_OR_GREATER
MC_26_1_OR_GREATER    MC_26_2_OR_GREATER    MC_26_3_OR_GREATER
```

## 5. 오류

빌드에 실패하면 `파일(줄,열): error MCS: 메시지` 형식으로 오류를 출력하고 종료 코드 1로 끝납니다. 데이터팩은 만들어지지 않습니다.

- `CSxxxx`로 시작하는 오류는 Roslyn이 보고한 C# 타입·문법 오류입니다.
- 그 밖의 오류는 마인크래프트(또는 대상 버전 중 일부)에서 표현할 수 없는 기능을 사용했다는 뜻입니다.
- `[1.20.1]`처럼 앞에 버전이 붙은 오류는 그 버전에만 해당합니다.

## 6. 월드에 적용

1. 출력 폴더(예: `out/hello`)를 `<월드>/datapacks/` 안에 복사합니다.
2. 게임에서 `/reload`를 실행합니다. 새로 만드는 월드에서는 자동으로 로드됩니다.
3. `/datapack list enabled`로 활성화되었는지 확인합니다. `file/hello`로 표시됩니다.

데이터팩이 로드되면:
- `Main()`이 자동으로 실행됩니다 (`/reload`할 때마다 다시 실행됩니다).
- `[Load]`가 붙은 메서드는 로드할 때, `[Tick]`이 붙은 메서드는 매 틱마다 실행됩니다.
- 모든 메서드는 `/function <ns>:<메서드 이름>`으로 직접 호출할 수도 있습니다. 이름은 snake_case로 바뀝니다: `SpawnBoss` → `/function hello:spawn_boss`.

## 7. 출력 구조

```
out/hello/
├── pack.mcmeta                    # 지원 범위 + 오버레이 목록
├── data/                          # 기본 레이어 (가장 낮은 버전 그룹)
│   ├── hello/functions/
│   │   ├── __load.mcfunction      # 초기화(스코어보드, 상수, static 필드) + Main 호출
│   │   ├── main.mcfunction
│   │   └── main/_if0.mcfunction   # 제어 흐름용으로 생성된 하위 함수
│   └── minecraft/tags/functions/load.json, tick.json
├── mcs_1_21/data/...              # 1.21+ 오버레이 (function/ 폴더)
├── mcs_1_21_2/data/...            # 1.21.2+ 오버레이
└── mcs_1_21_9/data/...            # 1.21.9+ 오버레이
```

어떤 오버레이가 생기는지는 코드에 따라 다릅니다. 결과가 같은 분기는 합쳐집니다.

### 이름 규칙

| 항목 | 이름 |
|---|---|
| 스코어보드 objective | `mcs.<ns>` |
| 지역 변수 | `$<메서드>.<변수>` (예: `$main.sum`) |
| static 필드 | `$static.<필드>` |
| 반환값 / 임시값 | `$<메서드>#ret`, `$<메서드>#t0` |
| Entity 변수 태그 | `mcs.<ns>.<메서드>.<변수>` |
| 스토리지 | `<ns>:mcs` |

디버깅할 때는 게임에서 `/scoreboard players get $main.sum mcs.hello`처럼 값을 확인할 수 있습니다.

## 8. 컴파일러 직접 빌드하기

```sh
cd src/Mcs.Compiler
dotnet build -c Release
dotnet bin/Release/net8.0/mcs.dll ../../examples/hello.mcs -o ../../out/hello
```

| 파일 | 역할 |
|---|---|
| `Versions.cs` | 버전 표와 버전별 기능. **버전 분기는 전부 여기서 결정됩니다.** |
| `Preprocessor.cs` | MC# 전용 문법(`use`, `new T : K x;`, `asm`)을 C#으로 변환 |
| `Prelude.cs` | 내장 라이브러리의 C# 선언 ([라이브러리 레퍼런스](libraries.md) 참고) |
| `Codegen.cs` | 구문 트리 → mcfunction (언어 핵심) |
| `Codegen.Libs.cs` | BlockLib / PlayerLib / TuiLib / SpawnEntity / SetWorldSpawn |
| `McData.cs`, `Data/mcdata.json` | 버전별 블록·아이템·엔티티·효과 데이터, 블록 클래스 생성 |
| `Datapack.cs` | 분기 병합, 오버레이, pack.mcmeta, 파일 출력 |
| `Program.cs` | CLI, `mcs.json` |

새 마인크래프트 버전을 지원하려면 `Versions.Releases`에 한 줄을 추가하세요. 새 버전에서 동작이 바뀌었다면 `TargetProfile`에 속성을 추가하면 됩니다.
