namespace EfApp;

// One implementation and no DI registration: resolved by uniqueness.
public interface IDiscountPolicy
{
    decimal Apply(decimal total);
}

public class StandardDiscountPolicy : IDiscountPolicy
{
    public decimal Apply(decimal total)
    {
        return total * 0.9m;
    }
}

// Two implementations and no DI registration: must stay unresolved.
public interface ITaxRule
{
    decimal Tax(decimal total);
}

public class SpainTaxRule : ITaxRule
{
    public decimal Tax(decimal total)
    {
        return total * 0.21m;
    }
}

public class PortugalTaxRule : ITaxRule
{
    public decimal Tax(decimal total)
    {
        return total * 0.23m;
    }
}

public class CheckoutService
{
    private readonly IDiscountPolicy _discount;
    private readonly ITaxRule _tax;

    public CheckoutService(IDiscountPolicy discount, ITaxRule tax)
    {
        _discount = discount;
        _tax = tax;
    }

    public decimal Total(decimal amount)
    {
        var net = _discount.Apply(amount);
        return net + _tax.Tax(net);
    }
}
