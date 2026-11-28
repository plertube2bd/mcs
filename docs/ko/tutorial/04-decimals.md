# 4. 소수

스코어보드는 정수만 저장합니다. MC#은 정수에 배율을 곱해 소수를 표현합니다(고정소수점). 두 가지 타입이 있습니다.

| 타입 | 저장 | 정밀도 | 범위 | 언제 쓰나 |
|---|---|---|---|---|
| `Milli` | 값 × 1000 | 0.001 (10진) | ±2147483 | 돈, 체력, 거리처럼 10진수로 딱 떨어져야 하는 값 |
| `Q16` | 값 × 65536 | 1/65536 (2진) | ±32768 | 각도, 삼각함수, 물리 계산 |

```csharp
Milli price = 2.5f;      // Milli 리터럴은 f를 붙입니다
Q16 angle = 0.5;         // Q16 리터럴은 그냥 씁니다
```

`float`, `double`도 같은 타입으로 동작하지만, 옛 이름이라 경고가 나옵니다.

## 계산 규칙

[`examples/t04_decimals.mcs`](examples/t04_decimals.mcs) 앞부분:

```csharp
Milli price = 2.5f;
Milli total = price * 3;                  // 7.500
Q16 half = 0.5;
m.Console.WriteLine($"total={total} half={half} int={(int)total}");
```

```
[t0] total=7.500 half=0.50000 int=7
```

| 연산 | 결과 |
|---|---|
| `+`, `-` | 정확 |
| `Milli * Milli` | 0.001 단위로 내림. 결과가 범위 안이면 중간에 넘치지 않음 (`3000f * 2` → 6000) |
| `Milli / Milli` | 0.001 단위로 0 쪽으로 버림. 나누는 수가 2147.483 이하면 정확 |
| `Q16 * Q16`, `Q16 / Q16` | 1/65536 단위로 같은 방식 |
| `(int)x` | 0 쪽으로 버림 (`(int)-2.5f` → −2) |
| 0으로 나누기 | `Milli`는 값이 그대로, `Q16`은 최댓값으로 포화 |

> `Q16`은 2진 분수라서 `0.15` 같은 10진 소수를 정확히 담지 못합니다 (0.15 → 0.1499939…). 10진수로 정확해야 하면 `Milli`를 쓰세요.

## 소수를 명령에 넣기

`asm $"…"` 안에 소수 변수를 그대로 쓰면 됩니다. **Python으로 값을 미리 만들 필요가 없습니다.**

```csharp
use MathLib;

static void Shake(int ticks)
{
    for (int t = 0; t < ticks; t++)
    {
        Q16 phase = (Q16)t * 1.7;
        Q16 yaw = Math.Sin(phase) * 0.8;      // -0.8 ~ 0.8도
        Q16 pitch = Math.Cos(phase) * 0.4;
        asm $"execute as @a at @s run tp @s ~ ~ ~ ~{yaw} ~{pitch}";
        Wait(1);
    }
}
```

`Shake(4)`의 결과 (26.3):

```
[t0] > tp @s ~ ~ ~ ~0.0 ~0.399993896484375
[t1] > tp @s ~ ~ ~ ~0.7933197021484375 ~-0.051544189453125
[t2] > tp @s ~ ~ ~ ~-0.204437255859375 ~-0.3867340087890625
[t3] > tp @s ~ ~ ~ ~-0.7406768798828125 ~0.1511688232421875
```

- 매 틱 새 값이 계산되어 명령에 들어갑니다. `Math.Sin`, `Math.Cos`, `Math.Tan`은 `Q16` 라디안을 받습니다.
- 숫자가 길게 나오는 것은 Minecraft가 소수를 Java `double`로 바꿔 쓰기 때문입니다. `tp`는 이 값을 그대로 받아들입니다.
- 런타임 값을 명령에 넣으려면 **함수 매크로**가 필요합니다. 그래서 이 예제는 1.20.2 이상에서만 빌드됩니다 (`// @mc 1.20.2-26.3`). 1.20.1을 포함하면 다음 오류가 납니다.

```
t04_decimals.mcs(22,16): error MCS: [1.20.1] Runtime values here need function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER', a constant, or raise the minimum target version.
```

### 값이 정해져 있다면: 컴파일 타임에 계산

흔들림 값이 고정이라면 컴파일할 때 계산하게 할 수 있습니다. 매크로가 필요 없으므로 1.20.1에서도 동작합니다.

```csharp
[Inline] static void Step(int i) { asm $"execute as @a at @s run tp @s ~ ~ ~ ~{i * 0.25f} ~"; }

foreach (int i in Unroll.Range(0, 4)) Step(i);
// → tp … ~0 ~ / ~0.25 ~ / ~0.5 ~ / ~0.75 ~
```

`Unroll.Range`와 `[Inline]`은 [7장](07-compile-time.md)에서 자세히 다룹니다.

## 정리

- 10진수로 정확해야 하면 `Milli`(`f` 붙임), 각도와 삼각함수는 `Q16`
- 소수 변수는 `asm $"…{x}…"`에 바로 넣을 수 있습니다 (런타임 값이면 1.20.2+)
- 관련 명세: [7.1 숫자](../spec.md#71-숫자와-불리언), [8.2 Milli](../spec.md#82-milli), [8.3 Q16](../spec.md#83-q16), [13.3 명령 보간](../spec.md#133-명령-보간-asm-)

다음: [5. 클래스와 객체](05-classes.md)
