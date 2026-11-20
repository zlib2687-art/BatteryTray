using System.Drawing.Drawing2D;

namespace BatteryTray;

/// <summary>
/// Pictogrammes vectoriels des types d'appareils, dessinés en GDI+ (aucune image embarquée).
/// Le dessin est défini dans une boîte de 64 × 40 unités ; <c>scale</c> l'agrandit ou la réduit.
/// Souris, clavier et micro reproduisent les modèles choisis par l'utilisateur (traits arrondis, sans remplissage).
/// </summary>
static class Glyphs
{
    public const float Width = 64, Height = 40;

    public static void Draw(Graphics g, DeviceKind kind, Color fg, float x, float y, float scale)
    {
        var saved = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(x, y);
            g.ScaleTransform(scale, scale);
            DrawCore(g, kind, fg);
        }
        finally { g.Restore(saved); }
    }

    static void DrawCore(Graphics g, DeviceKind kind, Color fg)
    {
        using var pen = new Pen(fg, 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thin = new Pen(fg, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(fg);

        switch (kind)
        {
            case DeviceKind.Headset:
                g.DrawArc(pen, 14, 3, 36, 36, 180, 180);                  // arceau
                FillRound(g, brush, new RectangleF(9, 20, 12, 16), 4);    // écouteur gauche
                FillRound(g, brush, new RectangleF(43, 20, 12, 16), 4);   // écouteur droit
                break;

            case DeviceKind.Mouse:
                // Souris vue de dessus : boîtier arrondi, séparation boutons / paume,
                // et un trait court depuis le haut (séparation gauche / droite).
                using (var body = RoundRect(new RectangleF(20, 1.5f, 24, 32), 11))
                    g.DrawPath(pen, body);
                g.DrawLine(thin, 21, 15, 43, 15);
                g.DrawLine(thin, 32, 3, 32, 10);
                break;

            case DeviceKind.Keyboard:
                // Clavier : contour avec une encoche en haut, quatre rangées de touches (la dernière a la barre d'espace).
                using (var outline = new GraphicsPath())
                {
                    outline.AddLine(41, 6, 53, 6);
                    outline.AddArc(47, 6, 12, 12, 270, 90);
                    outline.AddLine(59, 12, 59, 26);
                    outline.AddArc(47, 20, 12, 12, 0, 90);
                    outline.AddLine(53, 32, 11, 32);
                    outline.AddArc(5, 20, 12, 12, 90, 90);
                    outline.AddLine(5, 26, 5, 12);
                    outline.AddArc(5, 6, 12, 12, 180, 90);
                    outline.AddLine(11, 6, 36, 6);
                    g.DrawPath(thin, outline);
                }
                using (var key = new Pen(fg, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float[] cols = { 10, 20, 30, 40, 50 };           // début de chaque touche (largeur 6)
                    for (int row = 0; row < 3; row++)
                    {
                        float yy = 12.5f + row * 5f;
                        foreach (float cx in cols) g.DrawLine(key, cx, yy, cx + 6, yy);
                    }
                    g.DrawLine(key, 10, 27.5f, 16, 27.5f);           // dernière rangée : touche, barre d'espace, touche
                    g.DrawLine(key, 20, 27.5f, 46, 27.5f);
                    g.DrawLine(key, 50, 27.5f, 56, 27.5f);
                }
                break;

            case DeviceKind.Microphone:
                // Micro : capsule, étrier en U, pied et base.
                using (var capsule = RoundRect(new RectangleF(24, 1.5f, 16, 20), 8))
                    g.DrawPath(thin, capsule);
                g.DrawArc(thin, 17, 5, 30, 24, 0, 180);
                g.DrawLine(thin, 32, 29, 32, 33);
                g.DrawLine(thin, 24, 34, 40, 34);
                break;

            case DeviceKind.Gamepad:
                using (var body = RoundRect(new RectangleF(4, 6, 56, 28), 13))
                    g.DrawPath(pen, body);
                g.DrawLine(thin, 15, 20, 27, 20);                          // croix directionnelle
                g.DrawLine(thin, 21, 14, 21, 26);
                g.FillEllipse(brush, 40, 13, 6, 6);                        // boutons
                g.FillEllipse(brush, 48, 21, 6, 6);
                break;

            case DeviceKind.Webcam:
                g.DrawEllipse(pen, 18, 2, 28, 26);                         // bague de l'objectif
                g.FillEllipse(brush, 27, 10, 10, 10);                      // objectif
                g.DrawLine(pen, 32, 28, 32, 33);                           // pied
                g.DrawLine(pen, 22, 34, 42, 34);                           // base
                break;

            default:
                using (var body = RoundRect(new RectangleF(14, 6, 36, 26), 6))
                    g.DrawPath(pen, body);
                break;
        }
    }

    static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
    {
        using var p = RoundRect(r, radius);
        g.FillPath(b, p);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
