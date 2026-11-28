# 3. 실행 순서

데이터팩 버그의 상당수는 "무엇이 먼저 실행되는가"에서 나옵니다. 이 장에서는 MC#의 실행 순서 규칙을 익히고, 그 순서를 **생성된 명령을 읽지 않고** 확인하는 방법을 배웁니다.

## 규칙 세 가지

1. **한 메서드 안에서는 위에서 아래로.** 문장 하나가 끝나야 다음 문장이 시작합니다. (`Wait()`만 예외입니다. [6장](06-context.md))
2. **로드할 때:** objective 생성 → 정적 필드 초기화 → `[Load]` 메서드(소스 순서) → `Main()`
3. **매 틱:** 플레이어 이벤트(`[OnJoin]` 등) → `[Tick]` 메서드(소스 순서) → 예약된 `/schedule`(`Wait()`의 뒷부분 포함)

여러 파일이 있으면 "소스 순서"는 파일 순서(`mcs.json`의 폴더 안에서는 이름순)를 따릅니다.

## 예제: 판정한 다음에 0으로 되돌리기

플레이어가 달린 거리를 매 틱 확인하고, 2m 이상이면 메시지를 보낸 뒤, 거리를 0으로 되돌립니다. 핵심은 **판정이 초기화보다 먼저** 일어나야 한다는 점입니다.

[`examples/t03_order.mcs`](examples/t03_order.mcs):

```csharp
// @do 1 join Steve
// @do 3 stat Steve minecraft.custom:minecraft.sprint_one_cm 250
// @do 6 stat Steve minecraft.custom:minecraft.sprint_one_cm 40
// @ticks 8
use PlayerLib;
use TeamLib;

static Objective sprint = new Objective("tut.sprint", "minecraft.custom:minecraft.sprint_one_cm");

[Tick] static void Check()
{
    foreach (var p in Entities.All<Player>())
    {
        int cm = sprint[Player.Self];
        if (cm >= 200) Player.Self.SendMessage($"달리기 판정: {cm}cm");
        sprint[Player.Self] = 0;   // 판정한 다음에 0으로 되돌린다
    }
}
```

맨 위의 `// @...` 줄은 시뮬레이터용 대본입니다.

| 줄 | 뜻 |
|---|---|
| `// @do 1 join Steve` | 1틱에 Steve가 접속 |
| `// @do 3 stat Steve <통계> 250` | 3틱에 Steve의 달리기 통계가 250cm 늘어남 |
| `// @ticks 8` | 8틱 동안 실행 |

결과:

```
[t1] * join Steve
[t1] ! No score for @s in tut.sprint  ← scoreboard players get @s tut.sprint
[t3] * stat Steve minecraft.custom:minecraft.sprint_one_cm 250
[t3] to Steve: 달리기 판정: 250cm
[t6] * stat Steve minecraft.custom:minecraft.sprint_one_cm 40
```

이 출력으로 순서를 확인할 수 있습니다.

- 3틱: 250cm를 판정해 메시지가 나갔습니다. 초기화가 판정보다 먼저였다면 0cm를 읽었을 것이고, 메시지가 나가지 않았을 것입니다.
- 6틱: 40cm만 쌓였으므로 메시지가 없습니다. 3틱의 250cm가 남아 있었다면 290cm로 판정되었을 것입니다. 초기화가 동작한 것입니다.
- 1틱의 `!` 줄: Steve는 막 접속해서 아직 `tut.sprint` 스코어가 없습니다. 이때 MC#은 그 스코어를 **0으로 읽습니다**. 그래서 `cm`은 0이고, 판정은 거짓입니다. `!` 줄은 Minecraft가 "스코어가 없다"고 응답한 것을 보여 줄 뿐, 오류가 아닙니다.

> **미설정 스코어 규칙:** 엔티티나 플레이어에게 아직 없는 스코어는, 읽거나 비교하거나 계산할 때 항상 0으로 취급됩니다. `e.Hp == 0`은 참이고 `e.Hp != 0`은 거짓입니다. ([명세 7.4](../spec.md#74-미설정-스코어))

## 순서를 바꿔 보기

`sprint[Player.Self] = 0;`을 `if` 위로 옮기고 다시 실행해 보세요.

```sh
mcs run docs/ko/tutorial/examples/t03_order.mcs
```

3틱의 메시지가 사라집니다. 이렇게 **순서를 바꾸고 결과를 비교하는 것**이 순서를 확인하는 가장 확실한 방법입니다. 생성된 `.mcfunction`을 읽을 필요가 없습니다.

## 여러 `[Tick]` 메서드의 순서

```csharp
[Tick] static void Move() { … }     // 먼저
[Tick] static void Judge() { … }    // 나중
```

같은 틱 안에서 `Move`가 항상 `Judge`보다 먼저 실행됩니다. 파일이 다르면 파일 순서를 따릅니다. 순서가 중요하다면 `[Tick]`을 하나만 두고 그 안에서 차례로 부르는 것이 가장 분명합니다.

```csharp
[Tick] static void Tick() { Move(); Judge(); Reset(); }
```

## 정리

- 메서드 안: 위에서 아래로. 틱: 이벤트 → `[Tick]`(소스 순서) → `/schedule`
- 순서는 `mcs run`과 대본(`// @do`)으로 확인합니다. 순서를 바꿔 결과를 비교합니다.
- 아직 없는 스코어는 0으로 읽힙니다.
- 관련 명세: [10.1 로드 순서](../spec.md#101-로드-순서), [10.3 틱 순서](../spec.md#103-틱-순서), [10.4 이벤트](../spec.md#104-이벤트), [7.4 미설정 스코어](../spec.md#74-미설정-스코어)

다음: [4. 소수](04-decimals.md)
