using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(MyBatchPlot.Commands))]
[assembly: ExtensionApplication(typeof(MyBatchPlot.App))]

namespace MyBatchPlot
{
    // ---------------------------------------------------------------- logging
    public static class Log
    {
        public static string Path =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mbp_log.txt");

        public static void W(string s)
        {
            try
            {
                using (StreamWriter w = new StreamWriter(Path, true, Encoding.UTF8))
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
            }
            catch { }
        }

        public static void Clear()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    // ------------------------------------------------------------------ frame
    public class Frame
    {
        public int Index;
        public double X0, Y0, X1, Y1;
        public double W, H;
        public string Paper = "";
        public bool Matched;
        public bool Selected = false;   // 打开对话框时一个都不勾，由人手工挑
        public string Code = "";
        public string Title = "";
        public string Kind = "";
        public bool ForceFit;
        public bool PaperCertain;

        public double Area { get { return W * H; } }

        public bool Contains(Frame o)
        {
            const double t = 1.0;
            return o.X0 >= X0 - t && o.Y0 >= Y0 - t && o.X1 <= X1 + t && o.Y1 <= Y1 + t;
        }
    }

    public class Txt
    {
        public string S;
        public double X, Y, H;
    }

    // ------------------------------------------------------------- frame find
    public static class FrameFinder
    {
        static readonly Hashtable _seenTypes = new Hashtable();
        static readonly double[][] ISO = new double[][] {
            new double[]{1189, 841},   // A0
            new double[]{841,  594},   // A1
            new double[]{594,  420},   // A2
            new double[]{420,  297},   // A3
            new double[]{297,  210},   // A4
            new double[]{210,  148}    // A5
        };
        static readonly string[] ISOName = new string[] { "A0", "A1", "A2", "A3", "A4", "A5" };

        public static void MatchPaper(Frame f)
        {
            MatchPaperWH(f, f.W, f.H);
        }

        public static void MatchPaperWH(Frame f, double w, double h)
        {
            f.Matched = false;
            f.Paper = "";
            double lw = Math.Max(w, h);
            double sw = Math.Min(w, h);
            for (int i = 0; i < ISO.Length; i++)
            {
                double pl = ISO[i][0], ps = ISO[i][1];
                double tolL = Math.Max(6.0, pl * 0.015);
                double tolS = Math.Max(6.0, ps * 0.015);
                if (Math.Abs(lw - pl) <= tolL && Math.Abs(sw - ps) <= tolS)
                {
                    f.Matched = true;
                    f.PaperCertain = true;
                    f.Paper = ISOName[i];
                    return;
                }
            }
            double best = double.MaxValue; int bi = 0;
            for (int i = 0; i < ISO.Length; i++)
            {
                double d = Math.Abs(lw - ISO[i][0]) + Math.Abs(sw - ISO[i][1]);
                if (d < best) { best = d; bi = i; }
            }
            f.Paper = ISOName[bi];
        }

        // match a block frame that is drawn at an arbitrary scale (natural size may be an
        // inner frame like 410x287.5, or a scaled copy like 0.64 x A4)
        public static string PaperFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Match m = Regex.Match(name.ToUpperInvariant(), @"(^|[^A-Z0-9])A([0-5])([^0-9]|$)");
            if (m.Success) return "A" + m.Groups[2].Value;
            return null;
        }

        public static bool MatchPaperLoose(Frame f, double w, double h)
        {
            double lw = Math.Max(w, h), sw = Math.Min(w, h);
            if (lw < 50 || sw < 50) return false;
            int best = -1; double bestDiff = double.MaxValue; bool bestLand = true; double bestS = 1;
            for (int i = 0; i < ISO.Length; i++)
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    double pw = (mode == 0) ? ISO[i][0] : ISO[i][1];
                    double ph = (mode == 0) ? ISO[i][1] : ISO[i][0];
                    double s1 = lw / pw, s2 = sw / ph;
                    if (s1 <= 0) continue;
                    if (Math.Abs(s1 - s2) / s1 > 0.03) continue;      // must be proportional
                    double diff = Math.Abs(s1 - 1.0);
                    if (diff < bestDiff) { bestDiff = diff; best = i; bestLand = (mode == 0); bestS = s1; }
                }
            }
            if (best < 0) return false;
            f.Paper = ISOName[best];
            f.Matched = true;
            f.ForceFit = Math.Abs(bestS - 1.0) > 0.02;
            f.PaperCertain = Math.Abs(bestS - 1.0) <= 0.05;
            return true;
        }

        static Extents3d? DefExtents(BlockTableRecord btr, Transaction tr)
        {
            List<Extents3d> boxes = new List<Extents3d>();
            List<double> diag = new List<double>();
            foreach (ObjectId id in btr)
            {
                Entity e = null;
                try { e = (Entity)tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                if (e is AttributeDefinition) continue;
                try
                {
                    Extents3d ee = e.GeometricExtents;
                    boxes.Add(ee);
                    diag.Add(Math.Sqrt((ee.MaxPoint.X - ee.MinPoint.X) * (ee.MaxPoint.X - ee.MinPoint.X)
                                     + (ee.MaxPoint.Y - ee.MinPoint.Y) * (ee.MaxPoint.Y - ee.MinPoint.Y)));
                }
                catch { }
            }
            if (boxes.Count == 0) return null;

            // dirty blocks often carry a stray long line; drop boxes far bigger than the median
            double[] d = diag.ToArray();
            Array.Sort(d);
            double med = d[d.Length / 2];
            double limit = Math.Max(med * 10.0, 5.0);

            Extents3d? acc = null;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (diag[i] > limit) continue;
                if (acc == null) acc = boxes[i];
                else { Extents3d t = acc.Value; t.AddExtents(boxes[i]); acc = t; }
            }
            if (acc == null) return null;
            return acc;
        }

        static Extents3d TransformExtents(Extents3d e, Matrix3d m)
        {
            Point3d[] p = new Point3d[8];
            p[0] = e.MinPoint;
            p[1] = e.MaxPoint;
            p[2] = new Point3d(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.Z);
            p[3] = new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MinPoint.Z);
            p[4] = new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MinPoint.Z);
            p[5] = new Point3d(e.MaxPoint.X, e.MaxPoint.Y, e.MinPoint.Z);
            p[6] = new Point3d(e.MaxPoint.X, e.MinPoint.Y, e.MaxPoint.Z);
            p[7] = new Point3d(e.MinPoint.X, e.MaxPoint.Y, e.MaxPoint.Z);
            Extents3d r = new Extents3d(p[0].TransformBy(m), p[0].TransformBy(m));
            for (int i = 1; i < 8; i++) r.AddPoint(p[i].TransformBy(m));
            return r;
        }

        static Frame FromExtentsRaw(Extents3d e)
        {
            Frame f = new Frame();
            f.X0 = e.MinPoint.X; f.Y0 = e.MinPoint.Y;
            f.X1 = e.MaxPoint.X; f.Y1 = e.MaxPoint.Y;
            f.W = Math.Abs(f.X1 - f.X0);
            f.H = Math.Abs(f.Y1 - f.Y0);
            if (f.W < 0.01 || f.H < 0.01) return null;
            return f;
        }

        static Frame FromExtents(Extents3d e)
        {
            Frame f = new Frame();
            f.X0 = e.MinPoint.X; f.Y0 = e.MinPoint.Y;
            f.X1 = e.MaxPoint.X; f.Y1 = e.MaxPoint.Y;
            f.W = Math.Abs(f.X1 - f.X0);
            f.H = Math.Abs(f.Y1 - f.Y0);
            if (f.W < 50 || f.H < 50) return null;
            if (f.W * f.H < 4000) return null;
            double r = Math.Max(f.W, f.H) / Math.Min(f.W, f.H);
            if (r > 6.0) return null;
            MatchPaper(f);
            return f;
        }

        public class Seg { public double A, B, C; }   // horizontal: A=x1,B=x2,C=y ; vertical: A=y1,B=y2,C=x

        static List<Frame> FromLines(List<Line> lines)
        {
            const double tol = 0.8;
            List<Seg> hor = new List<Seg>();
            List<Seg> ver = new List<Seg>();
            for (int i = 0; i < lines.Count; i++)
            {
                Line ln = lines[i];
                double x1 = ln.StartPoint.X, y1 = ln.StartPoint.Y;
                double x2 = ln.EndPoint.X, y2 = ln.EndPoint.Y;
                if (Math.Abs(y1 - y2) <= tol && Math.Abs(x1 - x2) > 50)
                {
                    Seg s = new Seg();
                    s.A = Math.Min(x1, x2); s.B = Math.Max(x1, x2); s.C = (y1 + y2) / 2;
                    hor.Add(s);
                }
                else if (Math.Abs(x1 - x2) <= tol && Math.Abs(y1 - y2) > 50)
                {
                    Seg s = new Seg();
                    s.A = Math.Min(y1, y2); s.B = Math.Max(y1, y2); s.C = (x1 + x2) / 2;
                    ver.Add(s);
                }
            }
            List<Frame> res = new List<Frame>();
            if (hor.Count < 2 || ver.Count < 2) return res;

            Hashtable byRange = new Hashtable();
            for (int i = 0; i < hor.Count; i++)
            {
                Seg h = hor[i];
                string key = ((int)Math.Round(h.A / 2.0)).ToString() + "_" + ((int)Math.Round(h.B / 2.0)).ToString();
                ArrayList list = byRange[key] as ArrayList;
                if (list == null) { list = new ArrayList(); byRange[key] = list; }
                list.Add(h);
            }

            Hashtable byX = new Hashtable();
            for (int i = 0; i < ver.Count; i++)
            {
                Seg v = ver[i];
                string key = ((int)Math.Round(v.C / 2.0)).ToString();
                ArrayList list = byX[key] as ArrayList;
                if (list == null) { list = new ArrayList(); byX[key] = list; }
                list.Add(v);
            }

            foreach (DictionaryEntry de in byRange)
            {
                ArrayList list = (ArrayList)de.Value;
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        Seg a = (Seg)list[i], b = (Seg)list[j];
                        double ya = Math.Min(a.C, b.C), yb = Math.Max(a.C, b.C);
                        if (yb - ya < 50) continue;
                        if (!HasVertical(byX, a.A, ya, yb, tol)) continue;
                        if (!HasVertical(byX, a.B, ya, yb, tol)) continue;
                        Extents3d e = new Extents3d(new Point3d(a.A, ya, 0), new Point3d(a.B, yb, 0));
                        Frame f = FromExtents(e);
                        if (f != null) { f.Kind = "直线"; res.Add(f); }
                    }
                }
            }
            return res;
        }

        static bool HasVertical(Hashtable byX, double x, double y1, double y2, double tol)
        {
            int baseKey = (int)Math.Round(x / 2.0);
            for (int k = baseKey - 1; k <= baseKey + 1; k++)
            {
                ArrayList list = byX[k.ToString()] as ArrayList;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    Seg v = (Seg)list[i];
                    if (Math.Abs(v.C - x) > tol) continue;
                    if (v.A <= y1 + tol && v.B >= y2 - tol) return true;
                }
            }
            return false;
        }

        public static List<Frame> Find(Database db, Transaction tr, out int polyCount, out int blockCount, out int lineCount,
                                       bool hasScope, double sx0, double sy0, double sx1, double sy1)
        {
            List<Frame> raw = new List<Frame>();
            List<Txt> texts = new List<Txt>();
            List<Line> lines = new List<Line>();
            polyCount = 0; blockCount = 0; lineCount = 0;
            _seenTypes.Clear();

            BlockTableRecord space = GetCurrentSpace(db, tr);

            foreach (ObjectId id in space)
            {
                Entity ent = null;
                try { ent = (Entity)tr.GetObject(id, OpenMode.ForRead); }
                catch { continue; }
                if (ent == null) continue;
                if (string.Equals(ent.Layer, Marker.LayerName, StringComparison.OrdinalIgnoreCase)) continue;

                DBText dt = ent as DBText;
                if (dt != null)
                {
                    Txt t = new Txt();
                    t.S = dt.TextString;
                    t.X = dt.Position.X; t.Y = dt.Position.Y; t.H = dt.Height;
                    if (!string.IsNullOrEmpty(t.S)) texts.Add(t);
                    continue;
                }
                MText mt = ent as MText;
                if (mt != null)
                {
                    Txt t = new Txt();
                    t.S = StripMText(mt.Contents);
                    t.X = mt.Location.X; t.Y = mt.Location.Y; t.H = mt.TextHeight;
                    if (!string.IsNullOrEmpty(t.S)) texts.Add(t);
                    continue;
                }

                BlockReference br = ent as BlockReference;
                if (br != null)
                {
                    try
                    {
                        foreach (ObjectId ai in br.AttributeCollection)
                        {
                            AttributeReference ar = (AttributeReference)tr.GetObject(ai, OpenMode.ForRead);
                            Txt t = new Txt();
                            t.S = ar.TextString;
                            t.X = ar.Position.X; t.Y = ar.Position.Y; t.H = ar.Height;
                            if (!string.IsNullOrEmpty(t.S)) texts.Add(t);
                        }
                    }
                    catch { }

                    // window of the insert + natural size of the block definition
                    Frame bf = null, nat = null;
                    Extents3d? de = null;
                    try
                    {
                        BlockTableRecord btr2 = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        de = DefExtents(btr2, tr);
                        if (de != null)
                        {
                            nat = FromExtents(de.Value);
                            if (nat == null)
                            {
                                nat = new Frame();
                                nat.W = Math.Abs(de.Value.MaxPoint.X - de.Value.MinPoint.X);
                                nat.H = Math.Abs(de.Value.MaxPoint.Y - de.Value.MinPoint.Y);
                            }
                            if (!nat.Matched)
                            {
                                string pn = PaperFromName(btr2.Name);
                                if (pn != null) { nat.Paper = pn; nat.Matched = true; nat.PaperCertain = true; }
                                else MatchPaperLoose(nat, nat.W, nat.H);
                            }
                        }
                    }
                    catch (System.Exception exd) { Log.W("block def: " + exd.Message); }

                    try { bf = FromExtents(br.GeometricExtents); } catch { }
                    if (bf == null && de != null)
                    {
                        try { bf = FromExtentsRaw(TransformExtents(de.Value, br.BlockTransform)); }
                        catch (System.Exception ext2) { Log.W("block xform: " + ext2.Message); }
                    }

                    if (bf != null && nat != null && nat.Matched)
                    {
                        bf.Paper = nat.Paper;
                        bf.Matched = true;
                        bf.ForceFit = (Math.Abs(bf.W - nat.W) > 1.0 || Math.Abs(bf.H - nat.H) > 1.0);
                        bf.PaperCertain = nat.PaperCertain;
                    }
                    Log.W("INSERT de=" + (de == null ? "null" : (de.Value.MaxPoint.X - de.Value.MinPoint.X).ToString("0.#") + "x" + (de.Value.MaxPoint.Y - de.Value.MinPoint.Y).ToString("0.#"))
                        + " nat=" + (nat == null ? "null" : nat.W.ToString("0.#") + "x" + nat.H.ToString("0.#"))
                        + " bf=" + (bf == null ? "null" : bf.W.ToString("0.#") + "x" + bf.H.ToString("0.#")));
                    if (bf != null)
                        Log.W("block def=" + (nat == null ? "?" : nat.W.ToString("0.#") + "x" + nat.H.ToString("0.#"))
                            + " ins=" + bf.W.ToString("0.#") + "x" + bf.H.ToString("0.#")
                            + " paper=" + bf.Paper + " match=" + bf.Matched + " fit=" + bf.ForceFit);

                    if (bf != null && bf.Matched)
                    {
                        bf.Kind = "图块";
                        raw.Add(bf);
                        blockCount++;
                    }
                    continue;
                }

                Extents3d? ex = null;
                Polyline pl = ent as Polyline;
                if (pl != null)
                {
                    if (!pl.Closed || pl.NumberOfVertices != 4) continue;
                    polyCount++;
                    ex = pl.GeometricExtents;
                }
                else
                {
                    Polyline2d p2 = ent as Polyline2d;
                    if (p2 != null)
                    {
                        if (!p2.Closed) continue;
                        int n = 0;
                        foreach (ObjectId v in p2) { n++; if (n > 4) break; }
                        if (n != 4) continue;
                        polyCount++;
                        ex = p2.GeometricExtents;
                    }
                }
                if (ex != null)
                {
                    Frame f = FromExtents(ex.Value);
                    if (f != null) { f.Kind = "多段线"; raw.Add(f); }
                    continue;
                }

                Line ln = ent as Line;
                if (ln != null) { lines.Add(ln); continue; }

                string tn = ent.GetType().FullName;
                if (!_seenTypes.ContainsKey(tn))
                {
                    _seenTypes[tn] = 1;
                    Log.W("other entity type: " + tn + " dxf=" + ent.GetRXClass().DxfName);
                }
            }

            if (lines.Count > 0)
            {
                lineCount = lines.Count;
                raw.AddRange(FromLines(lines));
            }

            List<Frame> uniq = new List<Frame>();
            for (int i = 0; i < raw.Count; i++)
            {
                bool dup = false;
                for (int j = 0; j < uniq.Count; j++)
                {
                    if (Math.Abs(raw[i].X0 - uniq[j].X0) < 0.8 && Math.Abs(raw[i].Y0 - uniq[j].Y0) < 0.8 &&
                        Math.Abs(raw[i].X1 - uniq[j].X1) < 0.8 && Math.Abs(raw[i].Y1 - uniq[j].Y1) < 0.8)
                    { dup = true; break; }
                }
                if (!dup) uniq.Add(raw[i]);
            }

            List<Frame> outer = new List<Frame>();
            for (int i = 0; i < uniq.Count; i++)
            {
                bool inside = false;
                for (int j = 0; j < uniq.Count; j++)
                {
                    if (i == j) continue;
                    if (uniq[j].Contains(uniq[i]) && uniq[j].Area > uniq[i].Area * 1.05) { inside = true; break; }
                }
                if (!inside) outer.Add(uniq[i]);
            }

            // frames whose paper was only guessed by proportion follow the dominant certain paper
            Hashtable cntPaper = new Hashtable();
            string domPaper = ""; int domN = 0;
            for (int i = 0; i < outer.Count; i++)
                if (outer[i].PaperCertain)
                {
                    cntPaper[outer[i].Paper] = (cntPaper[outer[i].Paper] == null ? 1 : (int)cntPaper[outer[i].Paper] + 1);
                    if ((int)cntPaper[outer[i].Paper] > domN) { domN = (int)cntPaper[outer[i].Paper]; domPaper = outer[i].Paper; }
                }
            Log.W("inherit: dom=" + domPaper + " n=" + domN + " frames=" + outer.Count);
            if (domPaper.Length > 0)
            {
                double dl = 0, ds = 0;
                for (int i = 0; i < ISO.Length; i++) if (ISOName[i] == domPaper) { dl = ISO[i][0]; ds = ISO[i][1]; }
                for (int i = 0; i < outer.Count; i++)
                {
                    if (outer[i].PaperCertain) continue;
                    double lw = Math.Max(outer[i].W, outer[i].H), sw = Math.Min(outer[i].W, outer[i].H);
                    Log.W("  inherit cand " + outer[i].Paper + " aspect=" + (lw / sw).ToString("0.000") + " vs " + (dl / ds).ToString("0.000"));
                    if (Math.Abs(lw / sw - dl / ds) <= 0.04) { outer[i].Paper = domPaper; outer[i].Matched = true; }
                }
            }

            if (hasScope)
            {
                List<Frame> kept = new List<Frame>();
                for (int i = 0; i < outer.Count; i++)
                {
                    Frame f = outer[i];
                    double cx = (f.X0 + f.X1) / 2.0;
                    double cy = (f.Y0 + f.Y1) / 2.0;
                    bool centreIn = (cx >= sx0 && cx <= sx1 && cy >= sy0 && cy <= sy1);
                    double ox = Math.Min(f.X1, sx1) - Math.Max(f.X0, sx0);
                    double oy = Math.Min(f.Y1, sy1) - Math.Max(f.Y0, sy0);
                    double area = Math.Max(1e-6, (f.X1 - f.X0) * (f.Y1 - f.Y0));
                    double cover = (ox > 0 && oy > 0) ? (ox * oy / area) : 0;
                    if (centreIn || cover >= 0.5) kept.Add(f);
                }
                outer = kept;
            }

            outer.Sort(delegate(Frame a, Frame b)
            {
                if (Math.Abs(a.Y1 - b.Y1) > 5) return b.Y1.CompareTo(a.Y1);
                return a.X0.CompareTo(b.X0);
            });

            for (int i = 0; i < outer.Count; i++)
            {
                outer[i].Index = i + 1;
                ExtractNames(outer[i], texts);
            }
            return outer;
        }

        static readonly string[] Blacklist = new string[] {
            "设计","制图","审核","校对","批准","会签","日期","比例","图号","图名","材料","重量",
            "张数","第","单位","名称","签字","项目","阶段","版本","版次","说明","备注","数量","序号","共"
        };

        static void ExtractNames(Frame f, List<Txt> texts)
        {
            List<Txt> inside = new List<Txt>();
            for (int i = 0; i < texts.Count; i++)
            {
                Txt t = texts[i];
                if (t.X >= f.X0 - 2 && t.X <= f.X1 + 2 && t.Y >= f.Y0 - 2 && t.Y <= f.Y1 + 2)
                    inside.Add(t);
            }
            if (inside.Count == 0) return;

            string bestCode = "";
            int bestCodeScore = -1;
            string bestTitle = "";
            int bestTitleLen = -1;

            for (int i = 0; i < inside.Count; i++)
            {
                string s = Clean(inside[i].S);
                if (s.Length == 0) continue;

                if (IsCode(s))
                {
                    int score = (s.IndexOf('-') >= 0 ? 4 : 0) + (s.IndexOf('_') >= 0 ? 2 : 0)
                              + Math.Min(s.Length, 20);
                    if (score > bestCodeScore) { bestCodeScore = score; bestCode = s; }
                }

                int cn = CountChinese(s);
                if (cn >= 2 && s.Length <= 60 && !IsBlack(s))
                {
                    if (cn > bestTitleLen) { bestTitleLen = cn; bestTitle = s; }
                }
            }
            f.Code = bestCode;
            f.Title = bestTitle;
        }

        public static string StripMText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\\P", " ");
            if (s.StartsWith("{\\"))
            {
                int j = s.IndexOf(';');
                if (j > 0) s = s.Substring(j + 1);
            }
            s = s.Replace("{", "").Replace("}", "");
            s = Regex.Replace(s, @"\\[A-Za-z][^;]*;", "");
            return s;
        }

        public static string Clean(string s)
        {
            if (s == null) return "";
            s = StripMText(s);
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Replace(" ", "").Trim();
        }

        static bool IsCode(string s)
        {
            if (s.Length < 4 || s.Length > 40) return false;
            if (!Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9\-_./]*$")) return false;
            if (!Regex.IsMatch(s, @"[0-9]")) return false;
            return s.IndexOf('-') >= 0 || s.IndexOf('_') >= 0 || s.Length >= 6;
        }

        static bool IsBlack(string s)
        {
            for (int i = 0; i < Blacklist.Length; i++)
                if (s.IndexOf(Blacklist[i]) >= 0) return true;
            return false;
        }

        public static int CountChinese(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= 0x4e00 && s[i] <= 0x9fff) n++;
            return n;
        }

        public static BlockTableRecord GetCurrentSpace(Database db, Transaction tr)
        {
            string layout = LayoutManager.Current.CurrentLayout;
            if (string.Equals(layout, "Model", StringComparison.OrdinalIgnoreCase))
                return (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            ObjectId lid = LayoutManager.Current.GetLayoutId(layout);
            Layout lay = (Layout)tr.GetObject(lid, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
        }
    }

    // -------------------------------------------------------------- plot core
    public class PlotOptions
    {
        public string OutDir = "";
        public bool MakePdf = true;
        public bool SendPrinter = false;
        public string PrinterDevice = "Default Windows System Printer.pc3";
        public string StyleSheet = "";
        public bool StrictScale = false;
        public string NameMode = "file";
        public bool Merge = false;
    }

    public class RunResult
    {
        public int Sheets;
        public int Pdfs;
        public string MergeFile = "";
        public string Stats = "";
        public string Summary = "";
    }

    public static class Plotter
    {
        const string PdfDevice = "DWG To PDF.pc3";

        public class DeviceChoice
        {
            public string Device;
            public string Label;
        }

        // only devices AutoCAD itself can drive; a Windows printer that is not in
        // this list cannot be plotted to at all (no 32-bit driver)
        public static List<DeviceChoice> DeviceChoices()
        {
            List<DeviceChoice> list = new List<DeviceChoice>();
            try
            {
                Autodesk.AutoCAD.PlottingServices.PlotConfigManager.RefreshList(
                    Autodesk.AutoCAD.PlottingServices.RefreshCode.All);
            }
            catch { }
            try
            {
                Autodesk.AutoCAD.PlottingServices.PlotConfigInfoCollection devs =
                    Autodesk.AutoCAD.PlottingServices.PlotConfigManager.Devices;
                for (int i = 0; i < devs.Count; i++)
                {
                    string name = devs[i].DeviceName;
                    if (string.IsNullOrEmpty(name) || name == "无" || name == "None") continue;
                    DeviceChoice c = new DeviceChoice();
                    c.Device = name;
                    if (string.Equals(name, "Default Windows System Printer.pc3", StringComparison.OrdinalIgnoreCase))
                    {
                        string drv = "";
                        try
                        {
                            Autodesk.AutoCAD.PlottingServices.PlotConfig pc =
                                Autodesk.AutoCAD.PlottingServices.PlotConfigManager.SetCurrentConfig(name);
                            drv = pc.DriverName;
                        }
                        catch { }
                        c.Label = "Windows 默认打印机" + (drv.Length > 0 ? " - " + drv : "");
                    }
                    else if (name.ToLower().EndsWith(".pc3"))
                    {
                        c.Label = name;
                    }
                    else
                    {
                        c.Label = name + "（系统打印机）";
                    }
                    list.Add(c);
                }
            }
            catch (System.Exception ex) { Log.W("devices: " + ex.Message); }
            if (list.Count == 0)
            {
                DeviceChoice c = new DeviceChoice();
                c.Device = "Default Windows System Printer.pc3";
                c.Label = "Windows 默认打印机";
                list.Add(c);
            }
            return list;
        }

        static readonly double[][] ISOMM = new double[][] {
            new double[]{1189, 841}, new double[]{841, 594}, new double[]{594, 420},
            new double[]{420, 297}, new double[]{297, 210}, new double[]{148, 210}
        };
        static readonly string[] ISONameMM = new string[] { "A0", "A1", "A2", "A3", "A4", "A5" };

        // what sheets a plot device really offers (printer drivers name them
        // "A4", "User277" ... so match by page size instead of by name)
        static Hashtable _mediaCache = new Hashtable();

        static List<object[]> MediaList(string device)
        {
            List<object[]> list = _mediaCache[device] as List<object[]>;
            if (list != null) return list;
            list = new List<object[]>();
            try
            {
                Autodesk.AutoCAD.PlottingServices.PlotConfig pc =
                    Autodesk.AutoCAD.PlottingServices.PlotConfigManager.SetCurrentConfig(device);
                try { pc.RefreshMediaNameList(); } catch { }
                System.Collections.IEnumerable names = pc.CanonicalMediaNames as System.Collections.IEnumerable;
                if (names != null)
                {
                    foreach (object o in names)
                    {
                        string nm = o.ToString();
                        try
                        {
                            Autodesk.AutoCAD.PlottingServices.MediaBounds b = pc.GetMediaBounds(nm);
                            list.Add(new object[] { nm, b.PageSize.X, b.PageSize.Y });
                        }
                        catch { }
                    }
                }
            }
            catch (System.Exception ex) { Log.W("media list fail " + device + ": " + ex.Message); }
            _mediaCache[device] = list;
            return list;
        }

        static bool TryMediaBySize(PlotSettings ps, PlotSettingsValidator pv, string device, string paper, out string used)
        {
            used = null;
            double w = 420, h = 297;
            for (int i = 0; i < ISONameMM.Length; i++)
                if (ISONameMM[i] == paper) { w = ISOMM[i][0]; h = ISOMM[i][1]; break; }
            List<object[]> list = MediaList(device);
            string best = null;
            double bestErr = double.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                double mw = (double)list[i][1], mh = (double)list[i][2];
                for (int r = 0; r < 2; r++)
                {
                    double vw = (r == 0) ? mw : mh, vh = (r == 0) ? mh : mw;
                    double err = Math.Abs(vw - w) + Math.Abs(vh - h);
                    if (err < bestErr) { bestErr = err; best = (string)list[i][0]; }
                }
            }
            if (best == null || bestErr > 20) { Log.W("no media for " + paper + " on " + device); return false; }
            try
            {
                pv.SetPlotConfigurationName(ps, device, best);
                used = best;
                Log.W("media " + paper + " -> " + best + " (" + bestErr.ToString("0.#") + "mm off)");
                return true;
            }
            catch (System.Exception ex) { Log.W("media set fail " + best + ": " + ex.Message); return false; }
        }

        static string[] Candidates(string paper)
        {
            switch (paper)
            {
                case "A0": return new string[] { "ISO_FULL_BLEED_A0_(1189.00_x_841.00_MM)", "ISO_A0_(1189.00_x_841.00_MM)", "ISO_A0_(841.00_x_1189.00_MM)" };
                case "A1": return new string[] { "ISO_FULL_BLEED_A1_(841.00_x_594.00_MM)", "ISO_A1_(841.00_x_594.00_MM)", "ISO_A1_(594.00_x_841.00_MM)" };
                case "A2": return new string[] { "ISO_FULL_BLEED_A2_(594.00_x_420.00_MM)", "ISO_A2_(594.00_x_420.00_MM)", "ISO_A2_(420.00_x_594.00_MM)" };
                case "A3": return new string[] { "ISO_FULL_BLEED_A3_(420.00_x_297.00_MM)", "ISO_A3_(420.00_x_297.00_MM)", "ISO_A3_(297.00_x_420.00_MM)" };
                case "A4": return new string[] { "ISO_FULL_BLEED_A4_(210.00_x_297.00_MM)", "ISO_A4_(210.00_x_297.00_MM)", "ISO_A4_(297.00_x_210.00_MM)" };
                case "A5": return new string[] { "ISO_FULL_BLEED_A5_(148.00_x_210.00_MM)", "ISO_A5_(148.00_x_210.00_MM)", "ISO_A5_(210.00_x_148.00_MM)" };
            }
            return new string[] { "ISO_A3_(420.00_x_297.00_MM)" };
        }

        static string[] PaperOrder(string paper)
        {
            switch (paper)
            {
                case "A0": return new string[] { "A0" };
                case "A1": return new string[] { "A1", "A0" };
                case "A2": return new string[] { "A2", "A1", "A0" };
                case "A3": return new string[] { "A3", "A2", "A1", "A0" };
                case "A4": return new string[] { "A4", "A3", "A2", "A1", "A0" };
                case "A5": return new string[] { "A5", "A4", "A3", "A2", "A1", "A0" };
            }
            return new string[] { "A3" };
        }

        static bool TryMedia(PlotSettings ps, PlotSettingsValidator pv, string device, string paper, out string used)
        {
            used = null;
            string[] cand = Candidates(paper);
            for (int i = 0; i < cand.Length; i++)
            {
                try { pv.SetPlotConfigurationName(ps, device, cand[i]); used = cand[i]; return true; }
                catch (System.Exception ex) { Log.W("media fail " + cand[i] + " : " + ex.Message); }
            }
            // printer drivers don't know the ISO names above -> pick by page size
            try
            {
                pv.SetPlotConfigurationName(ps, device, null);
                if (TryMediaBySize(ps, pv, device, paper, out used)) return true;
                used = "<default>";
                return true;
            }
            catch (System.Exception ex) { Log.W("device fail " + device + " : " + ex.Message); return false; }
        }

        static void SetStyle(PlotSettings ps, PlotSettingsValidator pv, string style)
        {
            if (style == null || style.Length == 0) return;
            try { ps.PlotPlotStyles = true; } catch { }
            string[] tries = new string[] {
                style,
                Path.Combine(@"C:\Users\fanruocheng\AppData\Roaming\Autodesk\AutoCAD 2007\R17.0\chs\Plot Styles", style),
                Path.Combine(@"C:\Program Files (x86)\AutoCAD 2007\UserDataCache\Plot Styles", style)
            };
            for (int i = 0; i < tries.Length; i++)
            {
                try { pv.SetCurrentStyleSheet(ps, tries[i]); Log.W("style ok [" + tries[i] + "]"); return; }
                catch (System.Exception ex) { Log.W("style fail [" + tries[i] + "] : " + ex.Message); }
            }
        }

        static string SwapMediaName(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            Match m = Regex.Match(s, @"^(.*\()([0-9.]+)(_x_)([0-9.]+)(_MM\).*)$");
            if (!m.Success) return null;
            return m.Groups[1].Value + m.Groups[4].Value + m.Groups[3].Value + m.Groups[2].Value + m.Groups[5].Value;
        }

        static bool FitsAtOneToOne(PlotSettings ps, Frame f)
        {
            try
            {
                Point2d paper = ps.PlotPaperSize;
                Extents2d m = ps.PlotPaperMargins;
                double pw = paper.X - (m.MinPoint.X + m.MaxPoint.X);
                double ph = paper.Y - (m.MinPoint.Y + m.MaxPoint.Y);
                double fLong = Math.Max(f.W, f.H), fShort = Math.Min(f.W, f.H);
                double pLong = Math.Max(pw, ph), pShort = Math.Min(pw, ph);
                return fLong <= pLong + 0.5 && fShort <= pShort + 0.5;
            }
            catch { return false; }
        }

        public static PlotSettings MakeSettingsPublic(Layout lay, Frame f, string device, string style, bool strict)
        {
            return MakeSettings(lay, f, device, style, strict);
        }

        static PlotSettings MakeSettings(Layout lay, Frame f, string device, string style, bool strict)
        {
            PlotSettings ps = new PlotSettings(lay.ModelType);
            ps.CopyFrom(lay);
            PlotSettingsValidator pv = PlotSettingsValidator.Current;

            string used = null;
            string sheet = f.Paper;
            bool oneToOne = false;
            TryMedia(ps, pv, device, sheet, out used);
            oneToOne = FitsAtOneToOne(ps, f);
            if (f.ForceFit && !strict) oneToOne = false;
            if (!oneToOne && strict)
            {
                string[] order = PaperOrder(f.Paper);
                for (int i = 1; i < order.Length; i++)
                {
                    if (!TryMedia(ps, pv, device, order[i], out used)) continue;
                    if (FitsAtOneToOne(ps, f)) { sheet = order[i]; oneToOne = true; break; }
                }
                if (!oneToOne) TryMedia(ps, pv, device, f.Paper, out used);
            }
            string psStyle = "?";
            try { psStyle = ps.CurrentStyleSheet; } catch { }
            Log.W("frame " + f.Index + " style=[" + psStyle + "] paper=" + f.Paper + " sheet=" + sheet + " media=" + used + " 1to1=" + oneToOne);

            try { pv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters); }
            catch (System.Exception exu) { Log.W("units: " + exu.Message); }
            Extents2d ext = new Extents2d(f.X0, f.Y0, f.X1, f.Y1);
            try
            {
                pv.SetPlotWindowArea(ps, ext);
                pv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            }
            catch (System.Exception e1)
            {
                Log.W("orderA fail: " + e1.Message);
                pv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                pv.SetPlotWindowArea(ps, ext);
            }
            pv.SetPlotCentered(ps, true);

            // prefer the media variant whose orientation already matches the frame
            try
            {
                Point2d ps0 = ps.PlotPaperSize;
                bool frameLand0 = f.W >= f.H;
                bool paperLand0 = ps0.X >= ps0.Y;
                if (frameLand0 != paperLand0)
                {
                    string sw = SwapMediaName(used);
                    if (sw != null)
                    {
                        try { pv.SetCanonicalMediaName(ps, sw); Log.W("media swapped to " + sw); }
                        catch (System.Exception exs) { Log.W("swap fail " + sw + " : " + exs.Message); }
                    }
                }
            }
            catch (System.Exception exr) { Log.W("swap: " + exr.Message); }

            try
            {
                Point2d psz = ps.PlotPaperSize;
                bool frameLand = f.W >= f.H;
                bool paperLand = psz.X >= psz.Y;
                pv.SetPlotRotation(ps, frameLand == paperLand ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
            }
            catch (System.Exception ex) { Log.W("rotation: " + ex.Message); }

            try
            {
                pv.SetUseStandardScale(ps, true);
                pv.SetStdScaleType(ps, oneToOne ? StdScaleType.StdScale1To1 : StdScaleType.ScaleToFit);
            }
            catch (System.Exception ex) { Log.W("scale: " + ex.Message); }

            return ps;
        }

        public static void PlotOnePublic(PlotEngine pe, PlotProgressDialog ppd, ObjectId layoutId,
                            PlotSettings ps, string docName, bool toFile, string fileName)
        {
            PlotOne(pe, ppd, layoutId, ps, docName, toFile, fileName);
        }

        static void PlotOne(PlotEngine pe, PlotProgressDialog ppd, ObjectId layoutId,
                            PlotSettings ps, string docName, bool toFile, string fileName)
        {
            PlotInfo pi = new PlotInfo();
            pi.Layout = layoutId;
            pi.OverrideSettings = ps;
            PlotInfoValidator piv = new PlotInfoValidator();
            piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
            piv.Validate(pi);

            pe.BeginDocument(pi, docName, ppd, 1, toFile, fileName);
            PlotPageInfo ppi = new PlotPageInfo();
            pe.BeginPage(ppi, pi, true, null);
            pe.BeginGenerateGraphics(null);
            pe.EndGenerateGraphics(null);
            pe.EndPage(null);
            pe.EndDocument(null);
        }

        static string SafeFile(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            string bad = "\\/:*?\"<>|";
            for (int i = 0; i < bad.Length; i++) s = s.Replace(bad[i], '-');
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s;
        }

        public static RunResult Run(List<Frame> frames, PlotOptions opt)
        {
            RunResult res = new RunResult();
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            string layoutName = LayoutManager.Current.CurrentLayout;
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);

            int n = 0;
            for (int i = 0; i < frames.Count; i++) if (frames[i].Selected) n++;
            if (n == 0) { Log.W("nothing selected"); return res; }

            string baseName = "drawing";
            try { if (!string.IsNullOrEmpty(doc.Name)) baseName = Path.GetFileNameWithoutExtension(doc.Name); }
            catch { }
            if (string.IsNullOrEmpty(baseName)) baseName = "drawing";

            Hashtable used = new Hashtable();
            List<string> pdfFiles = new List<string>();
            Hashtable stat = new Hashtable();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout lay = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                string origStyle = "";
                if (opt.StyleSheet != null && opt.StyleSheet.Length > 0)
                {
                    origStyle = lay.CurrentStyleSheet;
                    try
                    {
                        PlotSettingsValidator.Current.SetCurrentStyleSheet(lay, opt.StyleSheet);
                        Log.W("style set -> " + lay.CurrentStyleSheet);
                    }
                    catch (System.Exception ex) { Log.W("style set fail: " + ex.Message); }
                }
                PlotProgressDialog ppd = null;
                try { ppd = new PlotProgressDialog(false, n, true); ppd.IsVisible = false; } catch { ppd = null; }

                PlotEngine pe = null;
                try
                {
                    pe = PlotFactory.CreatePublishEngine();
                    pe.BeginPlot(ppd, null);

                    for (int i = 0; i < frames.Count; i++)
                    {
                        Frame f = frames[i];
                        if (!f.Selected) continue;
                        res.Sheets++;
                        stat[f.Paper] = (stat[f.Paper] == null ? 1 : (int)stat[f.Paper] + 1);

                        if (opt.MakePdf)
                        {
                            string stem = "";
                            if (opt.NameMode == "code") stem = SafeFile(f.Code);
                            else if (opt.NameMode == "codetitle")
                                stem = SafeFile((SafeFile(f.Code) + " " + SafeFile(f.Title)).Trim());
                            if (stem.Length == 0) stem = baseName + "_" + f.Index.ToString("000");

                            string name = stem;
                            int k = 2;
                            while (used.ContainsKey(name.ToLower()))
                            {
                                name = stem + "_" + k.ToString();
                                k++;
                            }
                            used[name.ToLower()] = 1;

                            string outFile = Path.Combine(opt.OutDir, name + ".pdf");
                            PlotSettings ps = MakeSettings(lay, f, PdfDevice, opt.StyleSheet, opt.StrictScale);
                            PlotOne(pe, ppd, layoutId, ps, doc.Name, true, outFile);
                            pdfFiles.Add(outFile);
                            res.Pdfs++;
                            Log.W("PDF  " + outFile);
                        }

                        if (opt.SendPrinter)
                        {
                            PlotSettings ps2 = MakeSettings(lay, f, opt.PrinterDevice, opt.StyleSheet, opt.StrictScale);
                            PlotOne(pe, ppd, layoutId, ps2, doc.Name, false, "");
                            Log.W("PRN  " + f.Index);
                        }
                    }
                    pe.EndPlot(null);
                }
                finally
                {
                    if (pe != null) { try { pe.Destroy(); } catch { } }
                    if (ppd != null) { try { ppd.Destroy(); } catch { } }

                }
                // abort so the temporary plot style does not stay in the drawing
                try { tr.Abort(); } catch { tr.Commit(); }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("图纸统计：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("文件：" + doc.Name);
            int total = 0;
            string[] order = new string[] { "A0", "A1", "A2", "A3", "A4", "A5", "" };
            for (int i = 0; i < order.Length; i++)
            {
                if (stat[order[i]] == null) continue;
                int c = (int)stat[order[i]];
                total += c;
                sb.AppendLine("  " + (order[i].Length == 0 ? "其他" : order[i]) + "：" + c + " 张");
            }
            sb.AppendLine("  合计：" + total + " 张");
            res.Stats = sb.ToString();
            if (opt.MakePdf)
            {
                try { File.WriteAllText(Path.Combine(opt.OutDir, baseName + "_统计.txt"), res.Stats, Encoding.UTF8); }
                catch (System.Exception ex) { Log.W("stats write: " + ex.Message); }
            }

            if (opt.Merge && pdfFiles.Count > 0)
            {
                string mf = Path.Combine(opt.OutDir, baseName + "_合并.pdf");
                try
                {
                    PdfDocument outDoc = new PdfDocument();
                    for (int i = 0; i < pdfFiles.Count; i++)
                    {
                        PdfDocument inDoc = PdfReader.Open(pdfFiles[i], PdfDocumentOpenMode.Import);
                        for (int p = 0; p < inDoc.PageCount; p++)
                            outDoc.AddPage(inDoc.Pages[p]);
                        inDoc.Close();
                    }
                    outDoc.Save(mf);
                    outDoc.Close();
                    res.MergeFile = mf;
                    Log.W("MERGE " + mf);
                }
                catch (System.Exception ex)
                {
                    Log.W("merge error: " + ex.Message + "\n" + ex.StackTrace);
                    res.MergeFile = "ERR " + ex.Message;
                }
            }

            res.Summary = "完成：" + res.Sheets + " 张图纸"
                        + (res.Pdfs > 0 ? "，生成 " + res.Pdfs + " 个 PDF" : "")
                        + (res.MergeFile.Length > 0 && !res.MergeFile.StartsWith("ERR") ? "，已合并" : "")
                        + (opt.SendPrinter ? "，已发送打印机" : "");
            return res;
        }
    }

    // ---------------------------------------------------------------- preview
    public static class Previewer
    {
        public static string Png(Frame f, string style, int seq)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            string layoutName = LayoutManager.Current.CurrentLayout;
            ObjectId layoutId = LayoutManager.Current.GetLayoutId(layoutName);
            string outFile = Path.Combine(Path.GetTempPath(), "mbp_preview_" + seq + ".png");
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Layout lay = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                if (style != null && style.Length > 0)
                    try { PlotSettingsValidator.Current.SetCurrentStyleSheet(lay, style); } catch { }
                PlotProgressDialog ppd = null;
                try { ppd = new PlotProgressDialog(false, 1, true); ppd.IsVisible = false; } catch { }
                PlotEngine pe = null;
                try
                {
                    pe = PlotFactory.CreatePublishEngine();
                    pe.BeginPlot(ppd, null);
                    PlotSettings ps = Plotter.MakeSettingsPublic(lay, f, "PublishToWeb PNG.pc3", "", false);
                    Plotter.PlotOnePublic(pe, ppd, layoutId, ps, doc.Name, true, outFile);
                    pe.EndPlot(null);
                }
                catch (System.Exception ex) { Log.W("preview error: " + ex.Message + (char)10 + ex.StackTrace); }
                finally
                {
                    if (pe != null) { try { pe.Destroy(); } catch { } }
                    if (ppd != null) { try { ppd.Destroy(); } catch { } }
                }
                try { tr.Abort(); } catch { }
            }
            return File.Exists(outFile) ? outFile : null;
        }
    }

    // ------------------------------------------------- on-drawing frame marks
    public static class Marker
    {
        public const string LayerName = "MBP_图框标注";

        // bold TrueType style so the number stays legible at any zoom
        static ObjectId EnsureTextStyle(Database db, Transaction tr)
        {
            TextStyleTable st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (st.Has("MBP_NUM")) return st["MBP_NUM"];
            st.UpgradeOpen();
            TextStyleTableRecord r = new TextStyleTableRecord();
            r.Name = "MBP_NUM";
            try { r.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Arial", true, false, 0, 0); }
            catch { }
            ObjectId id = st.Add(r);
            tr.AddNewlyCreatedDBObject(r, true);
            return id;
        }

        static void EnsureLayer(Database db, Transaction tr)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(LayerName)) return;
            lt.UpgradeOpen();
            LayerTableRecord l = new LayerTableRecord();
            l.Name = LayerName;
            try
            {
                l.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8);
            }
            catch { }
            try { l.IsPlottable = false; } catch { }   // marks must never end up on paper
            lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
        }

        public static int EraseAll()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return 0;
            Database db = doc.Database;
            int n = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = FrameFinder.GetCurrentSpace(db, tr);
                space.UpgradeOpen();
                List<ObjectId> kill = new List<ObjectId>();
                foreach (ObjectId id in space)
                {
                    Entity e = null;
                    try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                    if (e == null) continue;
                    if (string.Equals(e.Layer, LayerName, StringComparison.OrdinalIgnoreCase))
                        kill.Add(id);
                }
                for (int i = 0; i < kill.Count; i++)
                {
                    Entity e = null;
                    try { e = tr.GetObject(kill[i], OpenMode.ForWrite) as Entity; } catch { continue; }
                    if (e == null || e.IsErased) continue;
                    e.Erase();
                    n++;
                }
                tr.Commit();
            }
            return n;
        }

        public static int Draw(List<Frame> frames)
        {
            EraseAll();
            if (frames == null || frames.Count == 0) return 0;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return 0;
            Database db = doc.Database;
            double span = 0;
            for (int i = 0; i < frames.Count; i++)
                span = Math.Max(span, Math.Max(frames[i].W, frames[i].H));
            int n = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr);
                ObjectId styleId = EnsureTextStyle(db, tr);
                BlockTableRecord space = FrameFinder.GetCurrentSpace(db, tr);
                space.UpgradeOpen();
                for (int i = 0; i < frames.Count; i++)
                {
                    Frame f = frames[i];
                    short col = (short)(f.Selected ? 1 : 9);

                    Polyline p = new Polyline(4);
                    p.AddVertexAt(0, new Point2d(f.X0, f.Y0), 0, 0, 0);
                    p.AddVertexAt(1, new Point2d(f.X1, f.Y0), 0, 0, 0);
                    p.AddVertexAt(2, new Point2d(f.X1, f.Y1), 0, 0, 0);
                    p.AddVertexAt(3, new Point2d(f.X0, f.Y1), 0, 0, 0);
                    p.Closed = true;
                    p.Layer = LayerName;
                    p.ColorIndex = col;
                    space.AppendEntity(p);
                    tr.AddNewlyCreatedDBObject(p, true);

                    // big number in the middle of the frame, with a solid backdrop so it
                    // stays readable over any geometry
                    double shortSide = Math.Min(f.W, f.H);
                    double h = span * 0.045;
                    if (h > shortSide * 0.32) h = shortSide * 0.32;
                    if (h < shortSide * 0.08) h = shortSide * 0.08;
                    if (h < 1.0) h = 1.0;

                    MText t = new MText();
                    t.Contents = f.Index.ToString();
                    t.TextHeight = h;
                    t.Attachment = AttachmentPoint.MiddleCenter;
                    t.Location = new Point3d((f.X0 + f.X1) / 2.0, (f.Y0 + f.Y1) / 2.0, 0);
                    try { t.TextStyle = styleId; } catch { }
                    try
                    {
                        t.Width = 0;
                        t.BackgroundFill = true;
                        t.UseBackgroundColor = false;
                        t.BackgroundFillColor = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 250);
                        t.BackgroundScaleFactor = 1.15;
                    }
                    catch { }
                    t.Layer = LayerName;
                    t.ColorIndex = col;
                    space.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                    Log.W("mark text " + t.Contents + " at " + t.Location.X.ToString("0.#") + "," + t.Location.Y.ToString("0.#")
                        + " h=" + t.TextHeight.ToString("0.#") + " col=" + t.ColorIndex
                        + " frame=" + f.X0.ToString("0.#") + "," + f.Y0.ToString("0.#") + " - " + f.X1.ToString("0.#") + "," + f.Y1.ToString("0.#"));
                    n++;
                }
                tr.Commit();
            }
            try { doc.Editor.Regen(); }
            catch { try { AcApp.UpdateScreen(); } catch { } }
            return n;
        }

        // bring every numbered box into view so the marks are visible right away
        public static void FitView(List<Frame> frames)
        {
            if (frames == null || frames.Count == 0) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].X0 < x0) x0 = frames[i].X0;
                if (frames[i].Y0 < y0) y0 = frames[i].Y0;
                if (frames[i].X1 > x1) x1 = frames[i].X1;
                if (frames[i].Y1 > y1) y1 = frames[i].Y1;
            }
            double w = Math.Max(x1 - x0, 1.0), h = Math.Max(y1 - y0, 1.0);
            using (ViewTableRecord v = doc.Editor.GetCurrentView())
            {
                double aspect = (v.Width > 1e-9) ? (v.Height / v.Width) : 0.7;
                double need = Math.Max(w, h / aspect) * 1.08;
                v.CenterPoint = new Point2d((x0 + x1) / 2.0, (y0 + y1) / 2.0);
                v.Width = need;
                v.Height = need * aspect;
                doc.Editor.SetCurrentView(v);
            }
        }
    }

    public class PreviewForm : Form
    {
        List<Frame> _frames; string _style; int _i;
        Hashtable _cache = new Hashtable();   // plotting each page is slow; keep what we made
        PictureBox _pic; Label _info; Button _prev, _next;

        public PreviewForm(List<Frame> frames, string style)
        {
            _frames = frames; _style = style;
            Text = "打印预览（自制版）";
            Width = 1000; Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("宋体", 9f);
            BackColor = Color.FromArgb(64, 64, 64);

            _pic = new PictureBox();
            _pic.SetBounds(10, 10, 965, 680);
            _pic.SizeMode = PictureBoxSizeMode.Zoom;
            _pic.BackColor = Color.White;
            Controls.Add(_pic);

            _info = new Label();
            _info.ForeColor = Color.White;
            _info.SetBounds(10, 700, 600, 20);
            Controls.Add(_info);

            _prev = new Button(); _prev.Text = "上一张"; _prev.SetBounds(700, 698, 80, 26);
            _prev.Click += delegate { if (_i > 0) { _i--; Show1(); } };
            Controls.Add(_prev);
            _next = new Button(); _next.Text = "下一张"; _next.SetBounds(790, 698, 80, 26);
            _next.Click += delegate { if (_i < _frames.Count - 1) { _i++; Show1(); } };
            Controls.Add(_next);
            Button close = new Button(); close.Text = "关闭"; close.SetBounds(880, 698, 80, 26);
            close.Click += delegate { Close(); };
            Controls.Add(close);

            Shown += delegate { Show1(); };
        }

        void Show1()
        {
            Frame f = _frames[_i];
            _info.Text = "第 " + (_i + 1) + " / " + _frames.Count + " 张    纸张 " + f.Paper
                       + "    " + f.W.ToString("0.#") + " x " + f.H.ToString("0.#");
            _prev.Enabled = _i > 0;
            _next.Enabled = _i < _frames.Count - 1;
            Cursor = Cursors.WaitCursor;
            try
            {
                string png = _cache[_i] as string;
                if (png == null || !File.Exists(png))
                {
                    _info.Text += "    （正在出图，稍等…）";
                    System.Windows.Forms.Application.DoEvents();
                    png = Previewer.Png(f, _style, _i + 1);
                    if (png == null) { _pic.Image = null; _info.Text += "    （预览失败，看日志）"; return; }
                    _cache[_i] = png;
                    _info.Text = "第 " + (_i + 1) + " / " + _frames.Count + " 张    纸张 " + f.Paper
                               + "    " + f.W.ToString("0.#") + " x " + f.H.ToString("0.#");
                }
                using (FileStream fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                {
                    System.Drawing.Image img = System.Drawing.Image.FromStream(fs);
                    if (_pic.Image != null) _pic.Image.Dispose();
                    _pic.Image = new Bitmap(img);
                }
            }
            catch (System.Exception ex) { Log.W("preview show: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }
    }

    // ------------------------------------------------------------------ dialog
    public class MainForm : Form
    {
        List<Frame> _frames;
        ListView _list;
        TextBox _dir;
        CheckBox _pdf;
        CheckBox _prn;
        CheckBox _strict;
        CheckBox _merge;
        ComboBox _nameMode;
        ComboBox _style;
        ComboBox _printer;
        CheckBox _markChk;
        bool _markOn;
        Button _ok;
        Button _preview;
        Button _cancel;
        ProgressBar _bar;
        Label _status;
        Label _title;
        bool _hasScope;
        bool _syncing;
        public int Action;   // 0=normal, 1=pick scope, 2=whole drawing

        public MainForm(List<Frame> frames, bool hasScope)
        {
            _frames = frames;
            _hasScope = hasScope;
            Text = "M-批打印（自制版）";
            Width = 900; Height = 640;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            // park the dialog at the screen edge so the numbered boxes stay visible
            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                Math.Max(0, Screen.PrimaryScreen.WorkingArea.Right - Width - 12),
                Math.Max(0, Screen.PrimaryScreen.WorkingArea.Top + 30));
            Font = new Font("宋体", 9f);

            _title = new Label();
            _title.Text = TitleText(frames.Count);
            _title.SetBounds(12, 10, 700, 20);
            Controls.Add(_title);

            _list = new ListView();
            _list.SetBounds(12, 32, 860, 300);
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.Columns.Add("序", 40);
            _list.Columns.Add("宽 x 高 (mm)", 120);
            _list.Columns.Add("纸张", 55);
            _list.Columns.Add("匹配", 55);
            _list.Columns.Add("来源", 65);
            _list.Columns.Add("图号", 180);
            _list.Columns.Add("图名", 300);
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                ListViewItem it = new ListViewItem(f.Index.ToString());
                it.SubItems.Add(f.W.ToString("0.#") + " x " + f.H.ToString("0.#"));
                it.SubItems.Add(f.Paper);
                it.SubItems.Add(f.Matched ? "标准" : "非标");
                it.SubItems.Add(f.Kind);
                it.SubItems.Add(f.Code);
                it.SubItems.Add(f.Title);
                it.Checked = f.Selected;
                it.Tag = f;
                _list.Items.Add(it);
            }
            Controls.Add(_list);
            _list.ItemChecked += delegate { if (!_syncing) SyncMarks(); };

            Button all = new Button(); all.Text = "全选"; all.SetBounds(12, 340, 60, 26);
            all.Click += delegate { SetAll(true); };
            Controls.Add(all);
            Button none = new Button(); none.Text = "全不选"; none.SetBounds(78, 340, 60, 26);
            none.Click += delegate { SetAll(false); };
            Controls.Add(none);

            Button pick = new Button(); pick.Text = "框选范围..."; pick.SetBounds(144, 340, 86, 26);
            pick.Click += delegate { Action = 1; Close(); };
            Controls.Add(pick);
            Button whole = new Button(); whole.Text = "全图"; whole.SetBounds(236, 340, 54, 26);
            whole.Click += delegate { Action = 2; Close(); };
            Controls.Add(whole);

            Label l2 = new Label(); l2.Text = "输出目录："; l2.SetBounds(300, 345, 70, 18);
            Controls.Add(l2);

            _dir = new TextBox();
            _dir.SetBounds(366, 342, 430, 22);
            _dir.Text = DefaultOutDir();
            Controls.Add(_dir);
            Button br = new Button(); br.Text = "浏览"; br.SetBounds(802, 340, 76, 26);
            br.Click += delegate
            {
                FolderBrowserDialog fb = new FolderBrowserDialog();
                fb.SelectedPath = _dir.Text;
                if (fb.ShowDialog() == DialogResult.OK) _dir.Text = fb.SelectedPath;
            };
            Controls.Add(br);

            _pdf = new CheckBox(); _pdf.Text = "生成 PDF"; _pdf.SetBounds(12, 378, 100, 22); _pdf.Checked = false;
            Controls.Add(_pdf);
            _prn = new CheckBox(); _prn.Text = "打印到打印机"; _prn.SetBounds(120, 378, 140, 22); _prn.Checked = true;
            Controls.Add(_prn);
            _merge = new CheckBox(); _merge.Text = "合并成一个 PDF"; _merge.SetBounds(280, 378, 150, 22); _merge.Checked = false; _merge.Enabled = false;
            Controls.Add(_merge);
            _pdf.CheckedChanged += delegate
            {
                _merge.Enabled = _pdf.Checked;
                if (!_pdf.Checked) _merge.Checked = false;
            };
            _strict = new CheckBox(); _strict.Text = "严格 1:1（装不下自动大一号纸）"; _strict.SetBounds(440, 378, 300, 22); _strict.Checked = false;
            Controls.Add(_strict);

            Label l4 = new Label(); l4.Text = "命名："; l4.SetBounds(12, 406, 60, 18);
            Controls.Add(l4);
            _nameMode = new ComboBox();
            _nameMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _nameMode.SetBounds(70, 402, 230, 22);
            _nameMode.Items.Add("原文件名+序号");
            _nameMode.Items.Add("图号（取不到用文件名）");
            _nameMode.Items.Add("图号+图名（取不到用文件名）");
            _nameMode.SelectedIndex = 0;
            Controls.Add(_nameMode);

            Label l6 = new Label(); l6.Text = "打印样式："; l6.SetBounds(320, 406, 70, 18);
            Controls.Add(l6);
            _style = new ComboBox();
            _style.DropDownStyle = ComboBoxStyle.DropDownList;
            _style.SetBounds(392, 402, 180, 22);
            _style.Items.Add("（沿用图形原有样式）");
            string[] sts = ListStyles();
            for (int i = 0; i < sts.Length; i++) _style.Items.Add(sts[i]);
            _style.SelectedIndex = 0;
            for (int i = 0; i < _style.Items.Count; i++)
                if ((string)_style.Items[i] == "monochrome.ctb") { _style.SelectedIndex = i; break; }
            Controls.Add(_style);

            Label l8 = new Label(); l8.Text = "打印机："; l8.SetBounds(12, 436, 60, 18);
            Controls.Add(l8);
            _printer = new ComboBox();
            _printer.DropDownStyle = ComboBoxStyle.DropDownList;
            _printer.SetBounds(70, 432, 520, 22);
            List<Plotter.DeviceChoice> devs = Plotter.DeviceChoices();
            string keep = LoadPrinter();
            int pickIdx = -1;
            for (int i = 0; i < devs.Count; i++)
            {
                _printer.Items.Add(devs[i].Label);
                if (keep.Length > 0 && string.Equals(devs[i].Device, keep, StringComparison.OrdinalIgnoreCase)) pickIdx = i;
            }
            if (pickIdx < 0)
                for (int i = 0; i < devs.Count; i++)
                    if (string.Equals(devs[i].Device, "Default Windows System Printer.pc3", StringComparison.OrdinalIgnoreCase))
                    { pickIdx = i; break; }
            if (pickIdx < 0 && devs.Count > 0) pickIdx = 0;
            _printer.SelectedIndex = pickIdx;
            _printer.Tag = devs;
            Controls.Add(_printer);

            _markChk = new CheckBox();
            _markChk.Text = "图纸上标注编号";
            _markChk.SetBounds(600, 432, 160, 22);
            _markOn = MarkSetting();
            _markChk.Checked = _markOn;
            Controls.Add(_markChk);
            _markChk.CheckedChanged += delegate
            {
                _markOn = _markChk.Checked;
                try
                {
                    if (_markOn) { Marker.Draw(_frames); Marker.FitView(_frames); }
                    else Marker.EraseAll();
                }
                catch (System.Exception ex) { Log.W("mark toggle: " + ex.Message); }
            };

            Label l3 = new Label();
            l3.Text = "图框：支持 闭合四点多段线 / 标准尺寸的图块 / 四条直线围成的矩形；被大框包住的小框自动忽略。";
            l3.SetBounds(12, 458, 860, 18);
            Controls.Add(l3);
            Label l5 = new Label();
            l5.Text = "比例：设备装不下时默认“适应图纸”；勾“严格 1:1”则换大一号纸保比例。";
            l5.SetBounds(12, 476, 860, 18);
            Controls.Add(l5);

            Label l7 = new Label();
            l7.Text = "默认一个都不勾：在左边勾要打的，勾中图上编号变红。灰=不打（可拖开本窗口看图）。";
            l7.SetBounds(12, 494, 860, 18);
            Controls.Add(l7);

            _bar = new ProgressBar(); _bar.SetBounds(12, 516, 700, 18); _bar.Minimum = 0; _bar.Maximum = 100;
            Controls.Add(_bar);
            _status = new Label(); _status.SetBounds(12, 536, 860, 18); _status.Text = "";
            Controls.Add(_status);

            _preview = new Button(); _preview.Text = "预览成品"; _preview.SetBounds(570, 560, 110, 28);
            _preview.Click += delegate { DoPreview(); };
            Controls.Add(_preview);

            _ok = new Button(); _ok.Text = "开始"; _ok.SetBounds(690, 560, 80, 28);
            _ok.Click += delegate { Start(); };
            Controls.Add(_ok);
            _cancel = new Button(); _cancel.Text = "取消"; _cancel.SetBounds(780, 560, 80, 28);
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);
            FormClosed += delegate { SaveUi(CurrentPrinter(), _markOn); };
        }

        string CurrentPrinter()
        {
            try
            {
                List<Plotter.DeviceChoice> devs = _printer.Tag as List<Plotter.DeviceChoice>;
                int i = _printer.SelectedIndex;
                if (devs != null && i >= 0 && i < devs.Count) return devs[i].Device;
            }
            catch { }
            return "Default Windows System Printer.pc3";
        }

        static string UiIni()
        {
            try { return Path.Combine(Path.GetDirectoryName(typeof(MainForm).Assembly.Location), "mbp_ui.ini"); }
            catch { return ""; }
        }

        static string LoadValue(string key)
        {
            try
            {
                string p = UiIni();
                if (p.Length == 0 || !File.Exists(p)) return "";
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i).Trim().ToLower() == key)
                        return line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return "";
        }

        static string LoadPrinter() { return LoadValue("printer"); }

        public static bool MarkSetting()
        {
            string v = LoadValue("marks");
            return !(v == "0");   // 默认在图纸上标编号
        }

        static void SaveUi(string dev, bool marks)
        {
            try
            {
                string p = UiIni();
                if (p.Length == 0) return;
                File.WriteAllText(p,
                    "printer=" + dev + "\r\n" + "marks=" + (marks ? "1" : "0") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static string[] ListStyles()
        {
            List<string> names = new List<string>();
            try
            {
                string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] dirs = new string[] {
                    Path.Combine(appdata, "Autodesk/AutoCAD 2007/R17.0/chs/Plot Styles"),
                    "C:/Program Files (x86)/AutoCAD 2007/UserDataCache/Plot Styles"
                };
                for (int i = 0; i < dirs.Length; i++)
                {
                    if (!Directory.Exists(dirs[i])) continue;
                    string[] files = Directory.GetFiles(dirs[i], "*.ctb");
                    for (int j = 0; j < files.Length; j++)
                    {
                        string nm = Path.GetFileName(files[j]);
                        if (!names.Contains(nm)) names.Add(nm);
                    }
                }
            }
            catch { }
            names.Sort();
            return names.ToArray();
        }

        string TitleText(int n)
        {
            return "识别到 " + n + " 个图框（范围：" + (_hasScope ? "已框选" : "整个图形") + "），勾选后开始：";
        }

        string DefaultOutDir()
        {
            try
            {
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (!string.IsNullOrEmpty(doc.Name) && File.Exists(doc.Name))
                    return Path.GetDirectoryName(doc.Name);
            }
            catch { }
            return Path.GetTempPath();
        }

        void SetAll(bool v)
        {
            _syncing = true;
            try { foreach (ListViewItem it in _list.Items) it.Checked = v; }
            finally { _syncing = false; }
            SyncMarks();
        }

        // frames carry the check state so the on-drawing boxes can be recoloured
        void SyncMarks()
        {
            foreach (ListViewItem it in _list.Items)
                ((Frame)it.Tag).Selected = it.Checked;
            if (!_markOn) return;
            try { Marker.Draw(_frames); }
            catch (System.Exception ex) { Log.W("mark sync: " + ex.Message); }
        }

        void DoPreview()
        {
            List<Frame> sel = new List<Frame>();
            foreach (ListViewItem it in _list.Items) if (it.Checked) sel.Add((Frame)it.Tag);
            if (sel.Count == 0) { MessageBox.Show("先勾选要预览的图框。"); return; }
            string style = (_style == null || _style.SelectedIndex <= 0) ? "" : _style.SelectedItem.ToString();
            PreviewForm pf = new PreviewForm(sel, style);
            pf.ShowDialog();
        }

        void Start()
        {
            int n = 0;
            foreach (ListViewItem it in _list.Items)
            {
                Frame f = (Frame)it.Tag;
                f.Selected = it.Checked;
                if (f.Selected) n++;
            }
            if (n == 0) { MessageBox.Show("没有勾选任何图框。"); return; }
            if (!_pdf.Checked && !_prn.Checked) { MessageBox.Show("请至少选择一种输出方式。"); return; }

            PlotOptions opt = new PlotOptions();
            opt.OutDir = _dir.Text.Trim();
            opt.MakePdf = _pdf.Checked;
            opt.SendPrinter = _prn.Checked;
            opt.StrictScale = _strict.Checked;
            opt.Merge = _merge.Checked;
            opt.NameMode = _nameMode.SelectedIndex == 1 ? "code" : (_nameMode.SelectedIndex == 2 ? "codetitle" : "file");
            opt.StyleSheet = (_style == null || _style.SelectedIndex <= 0) ? "" : _style.SelectedItem.ToString();
            opt.PrinterDevice = CurrentPrinter();
            SaveUi(opt.PrinterDevice, _markOn);

            if (opt.MakePdf && !Directory.Exists(opt.OutDir))
            {
                try { Directory.CreateDirectory(opt.OutDir); }
                catch { MessageBox.Show("输出目录无法创建：" + opt.OutDir); return; }
            }

            _ok.Enabled = false;
            _status.Text = "正在打印，请不要操作 CAD…";
            _bar.Style = ProgressBarStyle.Marquee;
            AcApp.UpdateScreen();
            try
            {
                RunResult r = Plotter.Run(_frames, opt);
                _bar.Style = ProgressBarStyle.Continuous;
                _status.Text = r.Summary;
                string msg = r.Summary;
                if (opt.MakePdf) msg += "\n\n输出目录：" + opt.OutDir;
                if (r.MergeFile.Length > 0 && !r.MergeFile.StartsWith("ERR")) msg += "\n合并文件：" + r.MergeFile;
                else if (r.MergeFile.StartsWith("ERR")) msg += "\n合并失败：" + r.MergeFile;
                MessageBox.Show(msg);
            }
            catch (System.Exception ex)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                Log.W("RUN ERROR " + ex.Message + "\n" + ex.StackTrace);
                _status.Text = "出错：" + ex.Message;
                MessageBox.Show("出错：" + ex.Message + "\n\n详细日志：" + Log.Path);
            }
            finally { _ok.Enabled = true; }
        }
    }

    // ------------------------------------------------------------ app init
    public class App : IExtensionApplication
    {
        const string MenuCmd =
            "(if (not (menugroup \"MBPMENU\")) (command \"_.MENULOAD\" \"C:/MSteel/MyBatchPlot/MBP.cui\")) " +
            "(menucmd \"P24=+MBPMENU.POP24\") ";

        static Timer _t;
        static int _tries;

        public void Initialize()
        {
            try
            {
                // during acaddoc the active document may not exist yet -> only schedule
                Schedule();
                Log.W("app init: menu scheduled");
            }
            catch (System.Exception ex) { Log.W("app init error: " + ex.Message); }
        }

        static void Schedule()
        {
            if (_t != null) return;
            _t = new Timer();
            _t.Interval = 3000;
            _t.Tick += new EventHandler(OnTick);
            _t.Start();
        }

        static void Send(string s)
        {
            if (AcApp.DocumentManager.Count <= 0) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute(s, true, false, false);
        }

        static void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (AcApp.DocumentManager.Count <= 0 || AcApp.DocumentManager.MdiActiveDocument == null)
                {
                    if (++_tries < 10) return;          // wait for a document, then give up
                }
                if (_t != null) { _t.Stop(); _t.Dispose(); _t = null; }
                Send(MenuCmd);
                Log.W("app init: menu sent (try " + _tries + ")");
            }
            catch (System.Exception ex) { Log.W("menu tick error: " + ex.Message); }
        }

        public void Terminate() { }
    }

    // ---------------------------------------------------------------- commands
    public class Commands
    {
        public static List<Frame> ScanScoped(bool hasScope, double x0, double y0, double x1, double y1,
                                             out int polys, out int blocks, out int lines)
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                List<Frame> fs = FrameFinder.Find(doc.Database, tr, out polys, out blocks, out lines,
                                                  hasScope, x0, y0, x1, y1);
                tr.Commit();
                return fs;
            }
        }

        static double[] RectOf(Point3d a, Point3d b)
        {
            return new double[] {
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y) };
        }

        static int CountIn(double[] r)
        {
            int p1, b1, l1;
            return ScanScoped(true, r[0], r[1], r[2], r[3], out p1, out b1, out l1).Count;
        }

        static bool PromptWindow(out double x0, out double y0, out double x1, out double y1)
        {
            x0 = 0; y0 = 0; x1 = 0; y1 = 0;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            PromptPointResult r1 = ed.GetPoint("\n指定框选范围的第一个角点: ");
            if (r1.Status != PromptStatus.OK) return false;
            PromptCornerOptions po = new PromptCornerOptions("\n指定对角点(拖拽框选): ", r1.Value);
            PromptPointResult r2 = ed.GetCorner(po);
            if (r2.Status != PromptStatus.OK) return false;

            // GetPoint/GetCorner return UCS coordinates; frame extents are WCS.
            // Try both readings and keep whichever actually contains frames.
            double[] raw = RectOf(r1.Value, r2.Value);
            double[] wcs = raw;
            try
            {
                Matrix3d m = ed.CurrentUserCoordinateSystem;
                wcs = RectOf(r1.Value.TransformBy(m), r2.Value.TransformBy(m));
            }
            catch (System.Exception ex) { Log.W("ucs transform: " + ex.Message); }

            if (raw[2] - raw[0] < 1.0 || raw[3] - raw[1] < 1.0) return false;

            int nRaw = -1, nWcs = -1;
            try { nRaw = CountIn(raw); } catch (System.Exception ex) { Log.W("count raw: " + ex.Message); }
            try { nWcs = CountIn(wcs); } catch (System.Exception ex) { Log.W("count wcs: " + ex.Message); }
            Log.W("pick raw=" + raw[0] + "," + raw[1] + "," + raw[2] + "," + raw[3] + " -> " + nRaw
                + " | wcs=" + wcs[0] + "," + wcs[1] + "," + wcs[2] + "," + wcs[3] + " -> " + nWcs);

            // GetPoint/GetCorner return UCS; WCS is the contract. Only fall back to the
            // raw reading when the transform finds nothing but raw does.
            double[] use = wcs;
            if (nWcs == 0 && nRaw > 0) use = raw;
            x0 = use[0]; y0 = use[1]; x1 = use[2]; y1 = use[3];
            return true;
        }

        static void ShowDialogCore(bool hasScope, double x0, double y0, double x1, double y1)
        {
            try
            {
                while (true)
                {
                    int polys, blocks, lines;
                    List<Frame> fs = ScanScoped(hasScope, x0, y0, x1, y1, out polys, out blocks, out lines);
                    Log.W("scope=" + hasScope + " polylines=" + polys + " blocks=" + blocks + " lines=" + lines + " frames=" + fs.Count);

                    if (fs.Count == 0)
                    {
                        string hint = hasScope ? "框选范围内没有识别到图框。" : "没有识别到图框。";
                        MessageBox.Show(hint + "\n\n支持：闭合四点多段线 / 标准尺寸图块 / 四条直线围成的矩形。");
                        return;
                    }

                    if (MainForm.MarkSetting())
                    {
                        try { Marker.Draw(fs); Marker.FitView(fs); }
                        catch (System.Exception ex) { Log.W("mark error: " + ex.Message); }
                    }

                    MainForm f = new MainForm(fs, hasScope);
                    f.ShowDialog();
                    Log.W("dialog closed action=" + f.Action);
                    if (f.Action == 1)
                    {
                        Marker.EraseAll();
                        double nx0, ny0, nx1, ny1;
                        if (PromptWindow(out nx0, out ny0, out nx1, out ny1))
                        { hasScope = true; x0 = nx0; y0 = ny0; x1 = nx1; y1 = ny1; }
                        continue;
                    }
                    if (f.Action == 2) { hasScope = false; continue; }
                    return;
                }
            }
            finally
            {
                try { Marker.EraseAll(); }
                catch (System.Exception ex) { Log.W("mark clear error: " + ex.Message); }
            }
        }

        [CommandMethod("MBP")]
        public void ShowDialog()
        {
            Log.Clear();
            Log.W("MBP start");
            try
            {
                // 先让人自己框范围，框完才识别（不再默认整图开框）
                double x0, y0, x1, y1;
                if (!PromptWindow(out x0, out y0, out x1, out y1)) return;
                ShowDialogCore(true, x0, y0, x1, y1);
            }
            catch (System.Exception ex)
            {
                Log.W("MBP ERROR " + ex.Message + "\n" + ex.StackTrace);
                MessageBox.Show("出错：" + ex.Message + "\n\n日志：" + Log.Path);
            }
        }

        [CommandMethod("MBPW")]
        public void ShowDialogWindow()
        {
            Log.Clear();
            Log.W("MBPW start");
            try
            {
                double x0, y0, x1, y1;
                if (!PromptWindow(out x0, out y0, out x1, out y1)) return;
                ShowDialogCore(true, x0, y0, x1, y1);
            }
            catch (System.Exception ex)
            {
                Log.W("MBPW ERROR " + ex.Message + "\n" + ex.StackTrace);
                MessageBox.Show("出错：" + ex.Message + "\n\n日志：" + Log.Path);
            }
        }

        [CommandMethod("MBPDIAG")]
        public void DiagStyle()
        {
            Log.Clear();
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId lid = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                Layout lay = (Layout)tr.GetObject(lid, OpenMode.ForWrite);
                Log.W("layout style=" + lay.CurrentStyleSheet + " plotstyles=" + lay.PlotPlotStyles);
                PlotSettingsValidator pv = PlotSettingsValidator.Current;
                try { pv.SetCurrentStyleSheet(lay, "monochrome.ctb"); Log.W("LAY ok -> " + lay.CurrentStyleSheet); }
                catch (System.Exception ex1) { Log.W("LAY fail: " + ex1.Message); }

                PlotSettings ps = new PlotSettings(lay.ModelType);
                ps.CopyFrom(lay);
                Log.W("ps style=" + ps.CurrentStyleSheet + " plotstyles=" + ps.PlotPlotStyles);
                try { ps.PlotPlotStyles = true; Log.W("ps flag=true ok"); }
                catch (System.Exception ex2) { Log.W("ps flag fail: " + ex2.Message); }
                try { pv.SetCurrentStyleSheet(ps, "monochrome.ctb"); Log.W("PS ok -> " + ps.CurrentStyleSheet); }
                catch (System.Exception ex3) { Log.W("PS fail: " + ex3.Message); }

                try { pv.SetCanonicalMediaName(ps, "ISO_A4_(210.00_x_297.00_MM)"); Log.W("media ok"); }
                catch (System.Exception ex4) { Log.W("media fail: " + ex4.Message); }
                try { pv.SetCurrentStyleSheet(ps, "monochrome.ctb"); Log.W("PS-after-media ok -> " + ps.CurrentStyleSheet); }
                catch (System.Exception ex5) { Log.W("PS-after-media fail: " + ex5.Message); }
                tr.Commit();
            }
        }

        [CommandMethod("MBPDEV")]
        public void DeviceProbe2()
        {
            Log.Clear();
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            int p1, b1, l1;
            List<Frame> fs = ScanScoped(false, 0, 0, 0, 0, out p1, out b1, out l1);
            Log.W("frames=" + fs.Count);
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                ObjectId lid = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                Layout lay = (Layout)tr.GetObject(lid, OpenMode.ForRead);
                List<Plotter.DeviceChoice> devs = Plotter.DeviceChoices();
                for (int i = 0; i < devs.Count; i++) Log.W("device[" + i + "] " + devs[i].Label + "  ->  " + devs[i].Device);
                string dev = "Default Windows System Printer.pc3";
                for (int i = 0; i < fs.Count; i++)
                {
                    try
                    {
                        PlotSettings ps = Plotter.MakeSettingsPublic(lay, fs[i], dev, "", false);
                        Point2d sz = ps.PlotPaperSize;
                        Log.W("frame " + fs[i].Index + " paper=" + fs[i].Paper + " media=" + ps.CanonicalMediaName
                            + " size=" + sz.X.ToString("0.#") + "x" + sz.Y.ToString("0.#"));
                    }
                    catch (System.Exception ex) { Log.W("frame " + fs[i].Index + " settings fail: " + ex.Message); }
                }
                tr.Abort();
            }
            AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage(" MBPDEV done -> " + Log.Path);
        }

        [CommandMethod("MBPBOX")]
        public void MarkBoxes()
        {
            Log.Clear();
            try
            {
                int p1, b1, l1;
                List<Frame> fs = ScanScoped(false, 0, 0, 0, 0, out p1, out b1, out l1);
                int n = Marker.Draw(fs);
                try { Marker.FitView(fs); } catch (System.Exception ex) { Log.W("fit error: " + ex.Message); }
                Log.W("mark: frames=" + fs.Count + " boxes=" + n);
                AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    "\n标注了 " + n + " 个图框（图层 " + Marker.LayerName + "，MBPCLEAR 可清除）");
            }
            catch (System.Exception ex) { Log.W("mark error: " + ex.Message); }
        }

        [CommandMethod("MBPCLEAR")]
        public void ClearMarks()
        {
            try
            {
                int n = Marker.EraseAll();
                AcApp.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n已清除 " + n + " 个图框标注");
            }
            catch (System.Exception ex) { Log.W("clear error: " + ex.Message); }
        }

        [CommandMethod("MBPPNG")]
        public void PngOne()
        {
            Log.Clear();
            try
            {
                int p1, b1, l1;
                List<Frame> fs = ScanScoped(false, 0, 0, 0, 0, out p1, out b1, out l1);
                Log.W("png: frames=" + fs.Count);
                if (fs.Count == 0) return;
                string png = Previewer.Png(fs[0], "monochrome.ctb", 1);
                Log.W("png out: " + png);
            }
            catch (System.Exception ex) { Log.W("png error: " + ex.Message); }
        }

        [CommandMethod("MBPRUN")]
        public void RunBatch()
        {
            Log.Clear();
            Log.W("MBPRUN start");
            try
            {
                string ini = Path.Combine(Path.GetTempPath(), "mbp_run.ini");
                PlotOptions opt = new PlotOptions();
                string only = "";
                string scopeArg = "";
                if (File.Exists(ini))
                {
                    foreach (string line in File.ReadAllLines(ini))
                    {
                        int p = line.IndexOf('=');
                        if (p <= 0) continue;
                        string k = line.Substring(0, p).Trim().ToLower();
                        string v = line.Substring(p + 1).Trim();
                        if (k == "outdir") opt.OutDir = v;
                        else if (k == "pdf") opt.MakePdf = (v == "1");
                        else if (k == "printer") opt.SendPrinter = (v == "1");
                        else if (k == "strict") opt.StrictScale = (v == "1");
                        else if (k == "merge") opt.Merge = (v == "1");
                        else if (k == "name") opt.NameMode = v;
                        else if (k == "style") opt.StyleSheet = v;
                        else if (k == "printerdev") opt.PrinterDevice = v;
                        else if (k == "select") only = v;
                        else if (k == "scope") scopeArg = v;
                    }
                }
                Log.W("outdir=" + opt.OutDir + " pdf=" + opt.MakePdf + " printer=" + opt.SendPrinter + " name=" + opt.NameMode + " merge=" + opt.Merge);

                bool hasScope = false;
                double sx0 = 0, sy0 = 0, sx1 = 0, sy1 = 0;
                if (scopeArg.Length > 0)
                {
                    string[] p4 = scopeArg.Split(',');
                    if (p4.Length == 4)
                    {
                        sx0 = double.Parse(p4[0].Trim(), CultureInfo.InvariantCulture);
                        sy0 = double.Parse(p4[1].Trim(), CultureInfo.InvariantCulture);
                        sx1 = double.Parse(p4[2].Trim(), CultureInfo.InvariantCulture);
                        sy1 = double.Parse(p4[3].Trim(), CultureInfo.InvariantCulture);
                        hasScope = true;
                    }
                }
                int polys, blocks, lines;
                List<Frame> fs = ScanScoped(hasScope, sx0, sy0, sx1, sy1, out polys, out blocks, out lines);
                Log.W("polylines=" + polys + " blocks=" + blocks + " lines=" + lines + " frames=" + fs.Count);
                for (int i = 0; i < fs.Count; i++)
                    Log.W("  #" + fs[i].Index + " " + fs[i].Kind + " " + fs[i].Paper + " code=" + fs[i].Code + " title=" + fs[i].Title);

                // 静默批打没指定 select= 就按整张图打
                if (!string.IsNullOrEmpty(only))
                {
                    Hashtable keep = new Hashtable();
                    foreach (string s in only.Split(',')) keep[s.Trim()] = 1;
                    for (int i = 0; i < fs.Count; i++) fs[i].Selected = keep.ContainsKey(fs[i].Index.ToString());
                }
                else
                {
                    for (int i = 0; i < fs.Count; i++) fs[i].Selected = true;
                }
                if (opt.MakePdf && !Directory.Exists(opt.OutDir)) Directory.CreateDirectory(opt.OutDir);

                RunResult r = Plotter.Run(fs, opt);
                Log.W("MBPRUN done=" + r.Sheets + " pdfs=" + r.Pdfs + " merge=" + r.MergeFile);
                Log.W(r.Stats);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "mbp_done.txt"),
                    r.Sheets.ToString() + "|" + r.Pdfs.ToString() + "|" + r.MergeFile, Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                Log.W("MBPRUN ERROR " + ex.Message + "\n" + ex.StackTrace);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mbp_done.txt"), "ERR " + ex.Message); } catch { }
            }
        }
    }
}
