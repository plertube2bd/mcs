# 6. 누가, 어디서 실행하는가

마인크래프트 명령은 항상 **누가(실행 주체, `@s`)** 와 **어디서(위치)** 를 가지고 실행됩니다. `~ ~ ~`, `@s`, 소리가 들리는 위치가 모두 여기에 달려 있습니다. MC#에서 이 맥락이 언제 무엇인지 정리합니다.

## 맥락 표

| 코드가 실행되는 곳 | `@s` (누가) | 위치 (어디서) |
|---|---|---|
| `Main()`, `[Load]`, `[Tick]` | 없음 (서버) | 월드 스폰 |
| 이벤트 메서드 (`[OnJoin]`, `[OnDeath]`, …) | 그 플레이어 | 그 플레이어 위치 |
| `foreach (var e in Entities.All<T>())` 본문 | 그 엔티티 | 바깥과 같음 (`at: true`면 그 엔티티 위치) |
| 엔티티 클래스의 인스턴스 메서드 | 그 엔티티 | 그 엔티티 위치 |
| `Wait()` 뒤의 코드 | 없음 (서버) | 월드 스폰 |
| `Execute.As(…).At(…).Run(…)` 람다 | `As`로 정한 대상 | `At`/`Positioned`로 정한 위치 (없으면 바깥과 같음) |

`Player.Self`와 `Position.Here`는 이 표의 "누가"와 "어디서"를 가리킵니다.

## 소리는 어디서 날까

| 호출 | 어디서 재생 | 누가 듣나 |
|---|---|---|
| `Sound.Play(id, pos)` | `pos` | `pos` 근처의 모든 플레이어 |
| `Sound.PlayTo(players, id)` | **각 플레이어 자신의 위치** | 그 플레이어만 (멀리 있어도 같은 크기로) |
| `Sound.Stop(players)` | — | 그 플레이어의 모든 소리 정지 |

`source` 매개변수의 값은 `SoundSource.Master`, `Music`, `Record`, `Weather`, `Block`, `Hostile`, `Neutral`, `Player`, `Ambient`, `Voice`입니다. 이런 열거형 값과 기본값은 [API 레퍼런스](../../api.md#soundlib)에서 모두 볼 수 있습니다.

## 예제

[`examples/t06_context.mcs`](examples/t06_context.mcs):

```csharp
// @do 1 join Steve
use PlayerLib;
use SoundLib;

[OnJoin] static void Hello(Player p)
{
    p.SendMessage("어서 와!");                              // 이 플레이어(@s)에게만
    Sound.PlayTo(Player.Self, "entity.player.levelup");     // 이 플레이어 위치에서, 이 플레이어만 듣는다
    Sound.Play("block.bell.use", GetWorldSpawn());          // 월드 스폰에서, 근처 모두가 듣는다
    Later();
}

static void Later()
{
    Wait(20);                                               // 1초 뒤: 서버가 월드 스폰에서 실행
    Sound.PlayTo(Player.All, "block.note_block.bell");      // Player.Self는 여기서 의미가 없다
}
```

결과 (1.21.9 이상, 발췌):

```
[t1] * join Steve
[t1] to Steve: 어서 와!
[t1] > playsound minecraft:entity.player.levelup master @s ~ ~ ~ 1 1
[t1] > playsound minecraft:block.bell.use master @a ~ ~ ~ 1 1
```

- `PlayTo`는 `@s ~ ~ ~`: 플레이어 자신에게, 자신의 위치에서 틉니다.
- `Play`는 `@a ~ ~ ~`를 월드 스폰 위치에서 실행합니다.
- 21틱의 종소리는 출력에 없습니다. 시뮬레이션이 그 전에 끝났기 때문입니다 (`// @ticks 25`를 추가하면 보입니다).

## `Wait()` 뒤에는 `@s`가 없습니다

`Wait()`는 메서드의 나머지를 `/schedule`로 미룹니다. `/schedule`로 실행되는 함수는 항상 서버가 월드 스폰에서 실행하므로, 이벤트에서 받은 플레이어 맥락이 사라집니다.

```csharp
[OnDeath] static void Died(Player p)
{
    p.SendMessage("3초 뒤 부활 효과");     // ✔ 맥락 있음
    Wait(60);
    Player.Self.Effect("minecraft:regeneration", 5);   // ✘ @s가 없어 아무 일도 없음
}
```

맥락이 필요하면 `Wait()` 전에 태그를 붙여 두고, 뒤에서 태그로 찾습니다.

```csharp
[OnDeath] static void Died(Player p)
{
    asm tag @s add revive;
    Wait(60);
    asm effect give @a[tag=revive] minecraft:regeneration 5;
    asm tag @a remove revive;
}
```

`asm` 대신 다음 절의 `Execute`로 태그 붙은 플레이어를 다시 `@s`로 만들 수도 있습니다.

```csharp
    Wait(60);
    Execute.AsPlayerObject(Player.WithTag("revive")).Run(() =>
    {
        Player.Self.Effect("minecraft:regeneration", 5);   // ✔ 다시 @s가 있음
        Player.Self.RemoveTag("revive");
    });
```

엔티티 변수(태그로 표현됨), 정적 필드, 지역 숫자 변수는 `Wait()` 뒤에도 이어집니다.

## 맥락 직접 바꾸기: `Execute`

맥락이 없는 곳(`Main()`, `[Tick]`, `Wait()` 뒤)에서 특정 플레이어를 `@s`로 삼거나 다른 위치에서 실행하려면 `Execute`를 씁니다. 마인크래프트의 `/execute`를 메서드 체인으로 쓴 것으로, 체인 하나가 `execute … run …` 명령 하나가 됩니다.

```csharp
Execute.AsPlayerObject(Player.All)                       // as @a
    .At(Player.Self)                                     // at @s
    .UnlessBlock("air", Position.Here.Offset(0, -1, 0))  // unless block ~ ~-1 ~ minecraft:air
    .Run(() => { Player.Self.SendMessage("땅 위에 서 있음"); });
```

규칙은 세 가지입니다.

- **한 문장으로 쓰고 `.Run(...)`으로 끝냅니다.** 단계마다 `;`를 붙이거나 체인을 변수에 담으면 컴파일 오류입니다.
- **`As` 뒤에서는 `Player.Self`(`@s`)가 그 대상입니다.** `At`이나 `Positioned`를 쓰면 `Position.Here`가 그 위치가 됩니다.
- **조건은 체인에서 놓인 자리에서 검사됩니다.** `As` 앞의 `If`는 한 번, `As` 뒤의 `If`는 대상마다 검사합니다.

| 단계 | `/execute` 하위 명령 |
|---|---|
| `As(대상)`, `AsPlayerObject(플레이어)` | `as` |
| `At(대상)`, `At(위치)`, `Positioned(위치)` | `at`, `positioned` |
| `In(Dimension.Nether)`, `Rotated(yaw, pitch)`, `RotatedAs(대상)`, `Facing(…)`, `AnchoredEyes()`, `Align("xz")` | 같은 이름의 하위 명령 |
| `IfBlock(id, 위치)`, `UnlessBlock(id, 위치)` | `if/unless block` |
| `IfEntity(대상)`, `UnlessEntity(대상)` | `if/unless entity` |
| `If(조건)`, `Unless(조건)` | MC#의 아무 bool 식 |
| `Sub("하위 명령")` | 그대로 출력 |

### 예제

[`examples/t06_execute.mcs`](examples/t06_execute.mcs):

```csharp
// @do 0 join Steve
// @do 0 join Alex
use PlayerLib;

static int greeted;

public static void Main()
{
    Wait(1);                                                  // 플레이어가 들어온 뒤
    Player.Named("Steve").AddTag("vip");

    // 서버 맥락(@s 없음)에서 각 플레이어를 @s로 바꿔 실행
    Execute.AsPlayerObject(Player.All).Run(() =>
    {
        greeted++;
        Player.Self.SendMessage("안녕!");                     // 이제 @s가 있다
    });
    m.Console.WriteLine($"인사한 수: {greeted}");

    // As 뒤의 조건은 플레이어마다 검사된다
    Execute.AsPlayerObject(Player.All)
        .If(Player.Self.HasTag("vip"))
        .Run(() => { Player.Self.SendMessage("VIP 전용 메시지"); });

    // 위치도 바꿀 수 있다: 각 플레이어 발밑이 공기가 아니면
    Execute.AsPlayerObject(Player.All)
        .At(Player.Self)
        .UnlessBlock("air", Position.Here.Offset(0, -1, 0))
        .Run(() => { Player.Self.SendMessage("땅 위에 서 있음"); });
}
```

결과:

```
[t0] * join Alex
[t0] * join Steve
[t1] to Alex: 안녕!
[t1] to Steve: 안녕!
[t1] 인사한 수: 2
[t1] to Steve: VIP 전용 메시지
[t1] > execute as @a at @s unless block ~ ~-1 ~ minecraft:air run tellraw @s ["",{"text":"땅 위에 서 있음"}]
```

- 람다는 플레이어마다 한 번씩 실행되어 `greeted`가 2가 됩니다.
- VIP 메시지는 태그가 있는 Steve만 받습니다. 이 조건은 명령 하나로 끝나지 않으므로, 컴파일러가 `As` 뒤의 나머지 체인을 하위 함수로 옮겨 플레이어마다 검사합니다.
- 시뮬레이터에는 월드(블록)가 없어서 블록 조건이 든 마지막 명령은 실행하지 않고 그대로 출력합니다. 게임에서는 발밑이 공기가 아닌 플레이어만 메시지를 받습니다.

## 정리

- 기본 맥락은 "서버, 월드 스폰". 이벤트와 엔티티 `foreach` 안에서만 `@s`가 있습니다.
- `Sound.PlayTo`는 각 플레이어 위치에서, 그 플레이어에게만 들립니다.
- `Wait()` 뒤에는 `@s`가 사라집니다. 태그로 넘깁니다.
- 맥락을 직접 바꾸려면 `Execute.As(…).At(…).Run(…)`을 씁니다. `As` 뒤의 조건은 대상마다 검사됩니다.
- 관련 명세: [10.2 기본 실행 맥락](../spec.md#102-기본-실행-맥락), [10.4 이벤트](../spec.md#104-이벤트), [10.5 Wait](../spec.md#105-wait), [부속서 B](../spec.md#부속서-b-참고-라이브러리-호출의-실행-맥락)

다음: [7. 컴파일 타임 코드](07-compile-time.md)
