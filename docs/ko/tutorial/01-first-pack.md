# 1. 첫 데이터팩

## 준비

| 필요한 것 | 비고 |
|---|---|
| .NET SDK 8 이상 | `~/.dotnet`에 설치하면 따로 설정할 필요가 없습니다 |
| Python 3 | `mcs run`(시뮬레이터)에 필요합니다. 빌드만 할 때는 필요 없습니다 |
| Minecraft Java 1.20.1 – 26.3 | 실제 게임에서 확인할 때 |

컴파일러를 `mcs` 명령으로 설치합니다.

```sh
tools/install.sh          # ~/.local/bin/mcs 가 생깁니다
```

설치하지 않고 저장소의 `./mcs`를 써도 똑같이 동작합니다.

## 첫 프로그램

`hello.mcs`:

```csharp
public static void Main()
{
    int sum = 0;
    for (int i = 1; i <= 10; i++)
        sum += i;
    m.Console.WriteLine($"1부터 10까지의 합: {sum}");
}
```

- `Main()`은 데이터팩이 로드될 때 자동으로 실행됩니다.
- `m.Console.WriteLine`은 모든 플레이어의 채팅에 출력합니다 (`tellraw @a`).
- 클래스를 쓰지 않아도 됩니다. 최상위에 쓴 메서드와 필드는 자동으로 클래스 하나에 들어갑니다.

## 게임을 켜지 않고 실행하기

```sh
mcs run hello.mcs
```

```
Build succeeded: out/hello (Minecraft 1.20.1 – 26.3)
  base           1.20.1-1.20.6      4 functions
  mcs_1_21       1.21-26.3          4 functions
Simulating out/hello (scores, storage, functions, schedule; blocks and positions are not simulated)
[t0] 1부터 10까지의 합: 55
```

`mcs run`은 데이터팩을 빌드한 다음 **시뮬레이터**로 실행합니다. 시뮬레이터는 스코어보드, 스토리지, 함수 호출, `/schedule`을 Minecraft와 똑같이 계산합니다. `[t0]`은 "0번째 틱"이라는 뜻입니다.

블록이나 소리처럼 시뮬레이터가 흉내 내지 않는 명령은 `> 명령` 형태로 출력됩니다. 그래서 출력이 곧 **실행된 명령의 기록**이 됩니다. 자세한 내용은 [8장](08-testing.md)에서 다룹니다.

## 게임에 넣기

```sh
mcs hello.mcs -o out/hello
```

1. `out/hello` 폴더를 `<월드>/datapacks/` 안에 복사합니다.
2. 게임에서 `/reload`를 입력합니다.
3. 채팅에 `1부터 10까지의 합: 55`가 뜹니다.

`/datapack list enabled`에 `file/hello`가 보이면 데이터팩이 켜진 것입니다.

## 프로젝트 파일

파일이 여러 개가 되면 `mcs.json`을 만듭니다.

```json
{
  "namespace": "hello",
  "sources": ["src"],
  "output": "out/hello",
  "minecraft": "1.20.1-26.3"
}
```

```sh
mcs build        # 빌드만
mcs run          # 빌드하고 시뮬레이터로 실행
```

`src` 폴더 안의 `.mcs` 파일은 이름순으로 합쳐져 하나의 데이터팩이 됩니다.

## 정리

- `mcs run 파일.mcs`로 게임 없이 결과를 봅니다.
- `mcs 파일.mcs -o 폴더`로 데이터팩을 만들어 월드에 넣습니다.
- 관련 명세: [6 프로그램 구조와 번역](../spec.md#6-프로그램-구조와-번역), [부속서 C 시뮬레이터](../spec.md#부속서-c-참고-시뮬레이터로-확인할-수-있는-범위)

다음: [2. 변수와 수명](02-variables.md)
