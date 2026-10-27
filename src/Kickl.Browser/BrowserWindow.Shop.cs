using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Kickl.Core.Markup;

namespace Kickl.Browser;

/// <summary>
/// Draws a page's <c>&lt;shop&gt;</c>: product cards with a price in satoshis and bitcoin, and a payment panel that shows the seller's address,
/// the exact amount and a BIP 21 payment link to copy into a wallet. The browser never sends a payment and cannot see whether one was made;
/// the panel says so. A product's file, if it has one, is an ordinary sealed blob further down the page that the seller's access key opens.
/// </summary>
public sealed partial class BrowserWindow
{
    private Control BuildShop(BrowserTab? tab, PageBlock block)
    {
        var address = block.Address ?? "";
        var stack = new StackPanel { Spacing = 9 };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(T(block.Text ?? "Shop", 15, Text, bold: true));
        head.Children.Add(Pill("BITCOIN SHOP", Accent, "#0b1b2c"));
        if (block.Testnet) head.Children.Add(Pill("TESTNET · NO REAL MONEY", Amber, "#241f0d"));
        stack.Children.Add(head);

        // Who is selling. A name sealed by the Kickl CA proves who holds the name, not that the goods are real.
        var seller = tab?.Ownership?.Verified == true
            ? ("Seller holds this name: VERIFIED OWNER (sealed by the Kickl CA). That proves who runs the page, not that the goods are as described.", Green)
            : tab?.Ownership?.Owned == true
                ? ("Seller holds a paid title for this name, but it is not CA-sealed yet.", Muted)
                : ("Seller identity is unverified: anyone can publish a page with any address. Pay only people you trust.", Amber);
        stack.Children.Add(T(seller.Item1, 9.5, seller.Item2).WithWrap());

        foreach (var product in block.Products)
            stack.Children.Add(BuildProductCard(block, product, address));

        stack.Children.Add(T("Kickl shows the address and amount; it cannot send a payment or see one. When you have paid, the seller confirms it and hands over any " +
                             "goods or access key. Bitcoin payments cannot be reversed.", 9, Muted).WithWrap());

        return new Border
        {
            Background = C("#0f1a14"),
            BorderBrush = C("#245437"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15),
            Child = stack
        };
    }

    private Control BuildProductCard(PageBlock shop, ProductInfo product, string address)
    {
        var panel = new StackPanel { Spacing = 7, IsVisible = false };
        var card = new StackPanel { Spacing = 6 };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(T(product.Name, 13.5, Text, bold: true));
        if (product.Description.Length > 0) title.Children.Add(T(product.Description, 11, "#a9bdd2").WithWrap());
        if (product.BlobId is not null)
            title.Children.Add(T("includes a sealed file · the seller sends you its access key after payment", 9, Green));
        row.Children.Add(title);

        var price = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right };
        price.Children.Add(T(BtcAmount.Sats(product.PriceSats) + " sats", 13, "#7ee787", bold: true));
        price.Children.Add(T(BtcAmount.Btc(product.PriceSats) + " BTC", 9.5, Muted, mono: true));
        Grid.SetColumn(price, 1);
        row.Children.Add(price);

        var buy = Btn("BUY", () => panel.IsVisible = !panel.IsVisible, accent: true);
        buy.Height = 30;
        buy.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buy, 2);
        row.Children.Add(buy);
        card.Children.Add(row);

        // The payment panel: what to send, where, and how to say what it is for
        var amount = BtcAmount.Btc(product.PriceSats);
        var uri = BtcAmount.PaymentUri(address, product.PriceSats, shop.Text ?? "Shop", product.Name);
        panel.Children.Add(T($"Send exactly {amount} BTC ({BtcAmount.Sats(product.PriceSats)} sats) to:", 10.5, Text));
        panel.Children.Add(Box(address, "#9ecbff", "#0a121a", mono: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        actions.Children.Add(Btn("COPY ADDRESS", () => CopyText(address, "address")));
        actions.Children.Add(Btn("COPY AMOUNT", () => CopyText(amount, "amount")));
        actions.Children.Add(Btn("COPY PAYMENT LINK", () => CopyText(uri, "payment link"), accent: true));
        panel.Children.Add(actions);
        panel.Children.Add(T(shop.Testnet
                ? "This is a TESTNET address: coins sent here are worth nothing and cannot be spent on the real network."
                : "Check the first and last characters of the address in your wallet before you confirm. For a large amount, send a small test payment first.",
            9.5, shop.Testnet ? Amber : Muted).WithWrap());
        if (product.BlobId is not null)
            panel.Children.Add(T("Your file is the sealed block right under this shop. Paste the access key the seller sends you into its unlock box.", 9.5, Muted).WithWrap());
        card.Children.Add(panel);

        return new Border
        {
            Background = C("#101a24"),
            BorderBrush = C("#25415c"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = card
        };
    }

    /// <summary>
    /// A live preview for the editor: the box holds a <c>&lt;shop&gt;</c>, so parse it with the real KML parser and show exactly what readers
    /// will see, or the exact reason it will be refused (a mistyped address says so here, before anyone can pay it).
    /// </summary>
    private Control ShopPreview(string fragment, string host)
    {
        try
        {
            var kml = $"<kml v=\"1\" site=\"{host}\"><head><title>preview</title></head><body><page>{fragment}</page></body></kml>";
            var page = Kml.Parse(kml, host);
            var shop = page.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Shop);
            return shop is null ? T("No <shop> found.", 11, Amber) : BuildShop(null, shop);
        }
        catch (KmlException ex)
        {
            return T("This shop will not publish yet: " + ex.Message, 11, Red).WithWrap();
        }
    }
}
