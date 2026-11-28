# 5. 클래스와 객체

MC#의 클래스는 C#처럼 쓰지만, 객체가 메모리가 아니라 **스코어 묶음**이라는 점이 다릅니다. 이 차이에서 생기는 규칙 세 가지만 알면 됩니다.

## 예제: 용량이 있는 가방

[`examples/t05_classes.mcs`](examples/t05_classes.mcs):

```csharp
class Bag
{
    public NArray<int> Items = new NArray<int>();
    public int Limit;
    public Bag(int limit) { Limit = limit; }

    public bool Put(int item)
    {
        if (Items.Length >= Limit) return false;
        Items.Add(item);
        return true;
    }
}

public static void Main()
{
    var bag = new Bag(2);
    bool a = bag.Put(10);
    bool b = bag.Put(20);
    bool c = bag.Put(30);                       // 가득 참
    m.Console.WriteLine($"{a} {b} {c} -> {bag.Items}");

    Bag same = bag;                             // 같은 객체
    same.Items.RemoveAt(0);
    m.Console.WriteLine($"bag={bag.Items} same={same.Items}");
}
```

```
[t0] True True False -> [10, 20]
[t0] bag=[20] same=[20]
```

인스턴스 메서드 안에서 `NArray` 필드를 바꾸면(`Items.Add`), 그 변경이 객체에 그대로 남습니다. 가방을 정적 클래스로 바꾸는 식의 우회는 필요 없습니다.

## 규칙 1: 필드는 스코어, 메서드는 함수

- `bag.Limit`은 스코어 하나(`$main.bag.Limit`)입니다. 읽고 쓰는 데 명령 하나가 듭니다.
- `NArray`, `NString` 필드는 NBT 스토리지에 있습니다.
- `bag.Put(10)`은 함수 호출입니다. 객체를 메서드 쪽으로 복사하고, 메서드가 객체를 바꿀 수 있으면 호출 뒤 다시 복사해 옵니다. 컴파일러가 "바꿀 수 있는가"를 판단하며, 다음은 모두 바꾸는 것으로 봅니다.
  - 필드 대입, `++`, `--`
  - `NArray` 필드의 `Add`, `Insert`, `RemoveAt`, `Pop`, `Clear`, 원소 대입
  - 다른 인스턴스 메서드 호출, `this`나 필드를 다른 메서드에 넘기기

## 규칙 2: 참조는 컴파일할 때 정해집니다

`Bag same = bag;`은 `same`을 `bag`과 **같은 객체**로 만듭니다 (C#과 같습니다). 하지만 실행 중의 조건으로 가리키는 객체를 바꿀 수는 없습니다.

```csharp
Bag x = bag;
if (k > 3) x = other;     // 오류: 조건에 따라 다른 객체를 가리킬 수 없습니다
```

이럴 때는 필드를 복사하거나, 두 객체를 각각 다룹니다. `null`도 없습니다.

## 규칙 3: 객체 안의 객체는 포함됩니다

```csharp
class Rect { public Vec Min = new Vec(0, 0); public Vec Max = new Vec(1, 1); }
```

`Rect` 하나에 `Vec` 두 개의 필드가 들어 있습니다. 그래서 클래스가 자기 자신을 필드로 가질 수 없습니다 (연결 리스트 불가). 가변 길이 데이터는 `NArray`를 씁니다.

## 언제 정적 클래스, 언제 인스턴스?

| 상황 | 선택 |
|---|---|
| 게임에 하나만 있는 상태 (현재 라운드, 설정) | 정적 클래스 (`static class Round { public static int number; }`) |
| 같은 모양의 데이터가 여러 개 (가방 여러 개, 팀별 정보) | 클래스의 인스턴스 |
| 월드의 엔티티마다 붙는 데이터 | 엔티티 클래스 (`[Entity(...)] partial class Unit : EntityObject`, [언어 가이드](../language.md#엔티티-클래스)) |

## 정리

- 객체는 스코어 묶음이고, 인스턴스 메서드에서 바꾼 필드(`NArray` 포함)는 객체에 반영됩니다.
- 참조는 컴파일 타임에 고정됩니다. 조건부 재대입과 `null`은 없습니다.
- 관련 명세: [12 클래스와 객체](../spec.md#12-클래스와-객체)

다음: [6. 누가, 어디서 실행하는가](06-context.md)
