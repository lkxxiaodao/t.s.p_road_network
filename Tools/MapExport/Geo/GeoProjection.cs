using System.Globalization;

namespace SignalPlanner.MapExport.Geo;

/// <summary>
/// 把 `.spmap` 的本地米制坐标（x 向东、y 向北）换算成 WGS84 经纬度或 UTM。
/// 原点由导出面板给出；不依赖任何第三方投影库。
/// </summary>
public sealed class GeoProjection
{
    private const double Wgs84A = 6378137.0;
    private const double Wgs84F = 1.0 / 298.257223563;
    private const double Wgs84E2 = Wgs84F * (2.0 - Wgs84F);
    private const double Wgs84Ep2 = Wgs84E2 / (1.0 - Wgs84E2);
    private const double UtmK0 = 0.9996;
    private const double UtmFalseEasting = 500000.0;
    private const double UtmFalseNorthingSouth = 10000000.0;

    private readonly double _lon0;
    private readonly double _lat0;
    private readonly GeoProjectionKind _kind;
    private readonly double _originEasting;
    private readonly double _originNorthing;

    private GeoProjection(double lon0, double lat0, GeoProjectionKind kind)
    {
        _lon0 = lon0;
        _lat0 = lat0;
        _kind = kind;

        if (kind == GeoProjectionKind.Utm)
        {
            UtmZone = ComputeUtmZone(lon0);
            IsNorth = lat0 >= 0.0;
            (double easting, double northing) = ForwardUtm(lat0, lon0, UtmZone, IsNorth);
            _originEasting = easting;
            _originNorthing = northing;
        }
        else
        {
            UtmZone = ComputeUtmZone(lon0);
            IsNorth = lat0 >= 0.0;
        }
    }

    public double OriginLon => _lon0;

    public double OriginLat => _lat0;

    public GeoProjectionKind Kind => _kind;

    public int UtmZone { get; }

    public bool IsNorth { get; }

    public static GeoProjection Create(double lon0, double lat0, GeoProjectionKind kind)
    {
        if (kind == GeoProjectionKind.Local)
        {
            return new GeoProjection(lon0, lat0, kind);
        }

        if (lon0 < -180.0 || lon0 > 180.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lon0), "经度必须在 [-180, 180]。");
        }

        if (lat0 < -85.0 || lat0 > 85.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lat0), "纬度必须在 [-85, 85]。");
        }

        return new GeoProjection(lon0, lat0, kind);
    }

    /// <summary>本地米 → WGS84 经纬度（度）。</summary>
    public (double Lon, double Lat) ToWgs84(double x, double y)
    {
        if (_kind == GeoProjectionKind.Utm)
        {
            return InverseUtm(_originEasting + x, _originNorthing + y, UtmZone, IsNorth);
        }

        return EquirectangularToWgs84(x, y);
    }

    /// <summary>本地米 → Shapefile 坐标（UTM 米 / WGS84 度 / 本地米）。</summary>
    public (double X, double Y) ToShapefile(double x, double y)
    {
        return _kind switch
        {
            GeoProjectionKind.Utm => (_originEasting + x, _originNorthing + y),
            GeoProjectionKind.Wgs84 => ToWgs84(x, y),
            _ => (x, y),
        };
    }

    /// <summary>Shapefile 的 .prj 文本。</summary>
    public string PrjWkt
    {
        get
        {
            return _kind switch
            {
                GeoProjectionKind.Utm => BuildUtmWkt(UtmZone, IsNorth),
                GeoProjectionKind.Wgs84 => BuildWgs84Wkt(),
                _ => "LOCAL_CS[\"SignalPlanner local meters\",UNIT[\"metre\",1.0]]",
            };
        }
    }

    /// <summary>OpenDRIVE geoReference 的 PROJ 字符串；Local 返回空串。</summary>
    public string Proj4
    {
        get
        {
            if (_kind == GeoProjectionKind.Local)
            {
                return string.Empty;
            }

            return $"+proj=tmerc +lat_0={F(_lat0)} +lon_0={F(_lon0)} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs";
        }
    }

    private (double Lon, double Lat) EquirectangularToWgs84(double x, double y)
    {
        double lat = _lat0 + y / Wgs84A * (180.0 / Math.PI);
        double cosLat = Math.Cos(_lat0 * Math.PI / 180.0);
        if (Math.Abs(cosLat) < 1e-12)
        {
            cosLat = 1e-12;
        }

        double lon = _lon0 + x / (Wgs84A * cosLat) * (180.0 / Math.PI);
        return (lon, lat);
    }

    private static int ComputeUtmZone(double lon)
    {
        int zone = (int)Math.Floor((lon + 180.0) / 6.0) + 1;
        return Math.Clamp(zone, 1, 60);
    }

    private static (double Easting, double Northing) ForwardUtm(double latDeg, double lonDeg, int zone, bool north)
    {
        double lat = latDeg * Math.PI / 180.0;
        double lon = lonDeg * Math.PI / 180.0;
        double lon0 = (zone * 6 - 183) * Math.PI / 180.0;

        double sinLat = Math.Sin(lat);
        double cosLat = Math.Cos(lat);
        double tanLat = Math.Tan(lat);

        double n = Wgs84A / Math.Sqrt(1.0 - Wgs84E2 * sinLat * sinLat);
        double t = tanLat * tanLat;
        double c = Wgs84Ep2 * cosLat * cosLat;
        double a = cosLat * (lon - lon0);

        double m = Wgs84A * ((1.0 - Wgs84E2 / 4.0 - 3.0 * Wgs84E2 * Wgs84E2 / 64.0 - 5.0 * Math.Pow(Wgs84E2, 3) / 256.0) * lat
            - (3.0 * Wgs84E2 / 8.0 + 3.0 * Wgs84E2 * Wgs84E2 / 32.0 + 45.0 * Math.Pow(Wgs84E2, 3) / 1024.0) * Math.Sin(2.0 * lat)
            + (15.0 * Wgs84E2 * Wgs84E2 / 256.0 + 45.0 * Math.Pow(Wgs84E2, 3) / 1024.0) * Math.Sin(4.0 * lat)
            - (35.0 * Math.Pow(Wgs84E2, 3) / 3072.0) * Math.Sin(6.0 * lat));

        double easting = UtmK0 * n * (a + (1.0 - t + c) * Math.Pow(a, 3) / 6.0
            + (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * Wgs84Ep2) * Math.Pow(a, 5) / 120.0)
            + UtmFalseEasting;

        double northing = UtmK0 * (m + n * tanLat * (a * a / 2.0
            + (5.0 - t + 9.0 * c + 4.0 * c * c) * Math.Pow(a, 4) / 24.0
            + (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * Wgs84Ep2) * Math.Pow(a, 6) / 720.0));

        if (!north)
        {
            northing += UtmFalseNorthingSouth;
        }

        return (easting, northing);
    }

    private static (double Lon, double Lat) InverseUtm(double easting, double northing, int zone, bool north)
    {
        double x = easting - UtmFalseEasting;
        double y = north ? northing : northing - UtmFalseNorthingSouth;

        double m = y / UtmK0;
        double mu = m / (Wgs84A * (1.0 - Wgs84E2 / 4.0 - 3.0 * Wgs84E2 * Wgs84E2 / 64.0 - 5.0 * Math.Pow(Wgs84E2, 3) / 256.0));
        double e1 = (1.0 - Math.Sqrt(1.0 - Wgs84E2)) / (1.0 + Math.Sqrt(1.0 - Wgs84E2));

        double phi1 = mu
            + (3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3) / 32.0) * Math.Sin(2.0 * mu)
            + (21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0) * Math.Sin(4.0 * mu)
            + (151.0 * Math.Pow(e1, 3) / 96.0) * Math.Sin(6.0 * mu)
            + (1097.0 * Math.Pow(e1, 4) / 512.0) * Math.Sin(8.0 * mu);

        double sinPhi1 = Math.Sin(phi1);
        double cosPhi1 = Math.Cos(phi1);
        double tanPhi1 = Math.Tan(phi1);

        double n1 = Wgs84A / Math.Sqrt(1.0 - Wgs84E2 * sinPhi1 * sinPhi1);
        double t1 = tanPhi1 * tanPhi1;
        double c1 = Wgs84Ep2 * cosPhi1 * cosPhi1;
        double r1 = Wgs84A * (1.0 - Wgs84E2) / Math.Pow(1.0 - Wgs84E2 * sinPhi1 * sinPhi1, 1.5);
        double d = x / (n1 * UtmK0);

        double lat = phi1 - (n1 * tanPhi1 / r1) * (d * d / 2.0
            - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * Wgs84Ep2) * Math.Pow(d, 4) / 24.0
            + (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * Wgs84Ep2 - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0);

        double lon0 = (zone * 6 - 183) * Math.PI / 180.0;
        double lon = lon0 + (d - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0
            + (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * Wgs84Ep2 + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0) / cosPhi1;

        return (lon * 180.0 / Math.PI, lat * 180.0 / Math.PI);
    }

    private static string BuildUtmWkt(int zone, bool north)
    {
        int epsg = (north ? 32600 : 32700) + zone;
        string hemisphere = north ? "N" : "S";
        double centralMeridian = zone * 6 - 183;
        double falseNorthing = north ? 0.0 : UtmFalseNorthingSouth;

        return string.Create(CultureInfo.InvariantCulture,
            $"PROJCS[\"WGS 84 / UTM zone {zone}{hemisphere}\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"latitude_of_origin\",0],PARAMETER[\"central_meridian\",{centralMeridian}],PARAMETER[\"scale_factor\",0.9996],PARAMETER[\"false_easting\",500000],PARAMETER[\"false_northing\",{falseNorthing}],UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"Easting\",EAST],AXIS[\"Northing\",NORTH],AUTHORITY[\"EPSG\",\"{epsg}\"]]");
    }

    private static string BuildWgs84Wkt()
    {
        return "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]]";
    }

    private static string F(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
}
