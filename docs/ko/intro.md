# Minecraft# — 데이터팩을 코드처럼 쓰는 언어

> 일반 사용자용 소개입니다. 개발 경험이 있다면 [전문가용 소개](intro-advanced.md)를 보세요.

## 한 줄 요약

**Minecraft#(MC#)** 은 C#과 거의 같은 문법으로 쓰면, 그대로 **바닐라 데이터팩**으로 바꿔 주는 언어입니다.
모드도, Fabric·Forge도 필요 없습니다. 만든 데이터팩은 월드 폴더에 넣기만 하면 동작합니다.

## 왜 만들었나요?

> **Q.** 그냥 모드 쓰면 되잖아요? 변수도 있고, 라이브러리도 많고, 최적화도 되는데.
> **A.** 모드를 쓰려면 플레이어 모두가 Fabric이나 Forge를 깔아야 하고, 맵 하나로 배포하기도 어렵거든요.
>
> **Q.** ...그럼 데이터팩을 감싸는 언어를 만들면 되잖아요?
> **A.** 그래서 만들었습니다.

## 직접 비교해 보세요

**좀비를 체력 100으로 소환하고, 채팅에 체력을 출력하기.**

MC#으로 쓰면 이렇습니다:

```csharp
use EntityLib;

public static void Main() {
    new Entity : Zombie z;
    z.maxHealth = 100f;
    z.Health = 100f;
    if (!m.SpawnEntityObject(z, GetWorldSpawn())) return;
    m.Console.WriteLine($"Spawned {z} with health {z.Health}");
}
```

똑같은 일을 데이터팩 명령어로 직접 쓰면, 파일 3개에 25줄이 필요합니다:

<details>
<summary>펼쳐 보기 (컴파일러가 실제로 만든 결과)</summary>

```mcfunction
# main.mcfunction
scoreboard players set $main#ctl mcs.zombie 0
scoreboard players set $main#t0 mcs.zombie 0
tag @e[tag=mcs.zombie.main.z] remove mcs.zombie.main.z
execute store success score $main#t0 mcs.zombie at @e[type=minecraft:marker,tag=mcs.zombie.spawn,limit=1] run summon minecraft:zombie ~ ~ ~ {Tags:["mcs.zombie.main.z"]}
execute if score $main#t0 mcs.zombie matches 1 run function zombie:main/_init0
execute unless score $main#t0 mcs.zombie matches 1 run scoreboard players set $main#ctl mcs.zombie 1
execute if score $main#ctl mcs.zombie matches 0 run function zombie:main/_cont1

# main/_init0.mcfunction
attribute @e[tag=mcs.zombie.main.z,limit=1] minecraft:max_health base set 100
data modify entity @e[tag=mcs.zombie.main.z,limit=1] Health set value 100f

# main/_cont1.mcfunction (16줄 — 체력을 소수점까지 출력하는 부분)
execute store result score $main#t1 mcs.zombie run data get entity @e[tag=mcs.zombie.main.z,limit=1] Health 1000
...
tellraw @a ["",{"text":"Spawned "},{"selector":"@e[tag=mcs.zombie.main.z,limit=1]"}, ...]
```

</details>

어느 쪽이 읽기 쉬운가요? 같은 일을, 더 적게, 더 쉽게 할 수 있습니다.

## 무엇을 할 수 있나요?

- **엔티티·블록·플레이어를 객체처럼 다루기** — `z.Health = 100f;`, `s.Facing = Facing.East;`
- **변수, 계산, `if`·`for`·`while`·`switch`** — 스코어보드 명령어를 직접 쓸 필요가 없습니다
- **플레이어 인벤토리, 효과, 경험치, 게임 모드 제어**
- **클릭할 수 있는 채팅 버튼과 메뉴** 만들기
- **여러 버전에서 동작** — 데이터팩 하나가 1.20.1부터 26.3까지 지원합니다. 버전마다 명령어가 달라지는 부분은 컴파일러가 알아서 맞춥니다.
- **게임을 켜기 전에 실수 잡기** — 오타, 없는 블록 이름, 그 버전에 없는 엔티티 등은 빌드할 때 줄 번호와 함께 알려 줍니다.
- **게임 없이 실행해 보기** — `mcs run`이 데이터팩을 시뮬레이터로 돌려, 스코어 계산과 실행 순서를 게임 밖에서 확인할 수 있습니다.
- **VS Code, Visual Studio에서 편하게 작성** — C# 문법이라 메모장 대신 익숙한 편집기를 쓸 수 있습니다.
- **AI와 함께 쓰기 좋음** — AI는 수천 줄짜리 데이터팩 명령어보다 C#을 훨씬 잘 읽고 씁니다.
- **완전 무료** — MIT 라이선스입니다.

## 필요한 것

| 누가 | 필요한 것 |
|---|---|
| **만드는 사람** | .NET SDK 8 이상, Minecraft Java Edition 1.20.1 – 26.3 |
| **플레이하는 사람** | Minecraft Java Edition 1.20.1 – 26.3 **뿐** (모드·로더 필요 없음) |

## 3단계로 시작하기

1. `hello.mcs` 파일에 위 예제를 붙여 넣습니다.
2. 컴파일합니다.
   ```sh
   ./mcs hello.mcs -o out/hello
   ```
3. `out/hello` 폴더를 `<월드>/datapacks/` 안에 복사하고, 게임에서 `/reload`를 입력합니다.

끝입니다. 좀비가 월드 스폰에 나타나고 채팅에 메시지가 뜹니다.
자세한 방법은 [컴파일하기](compiling.md)를 보세요.

## 아직 안 되는 것

정직하게 말씀드리면, 데이터팩 위에서 돌아가는 언어라서 C#의 모든 것을 할 수는 없습니다.

- 실행 중에 바뀌는 문자열(`NString`)은 비교·자르기는 모든 버전에서 되지만, 붙이기는 1.20.2 이상에서만 됩니다.
- 소수는 `Milli`가 **소수점 셋째 자리**, `Q16`이 **1/65536**(약 소수점 다섯째 자리)까지 정확하고, `Q16`의 범위는 약 ±32768입니다.
- `Wait()`로 기다린 뒤에는 실행 주체(`@s`)와 위치가 이어지지 않습니다.
- 함수가 자기 자신을 다시 부르는 **재귀**는 되지만, 호출마다 변수를 저장·복원하므로 아주 깊게 들어가면 명령어 수 제한에 걸릴 수 있습니다.
- 1.20.1 미만, 26.3 초과 버전은 아직 지원하지 않습니다 (새 버전은 계속 추가할 예정입니다).

## 더 알아보기

- [튜토리얼](tutorial/README.md) — 처음 배우는 순서대로
- [언어 명세](spec.md) — 동작의 정확한 규칙
- [컴파일하기](compiling.md)
- [라이브러리 레퍼런스](libraries.md) · [API 레퍼런스](../api.md)
- [언어 가이드](language.md)
- 소스 코드: https://github.com/plertube2bd/mcs (MIT 라이선스)
