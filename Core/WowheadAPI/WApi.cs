﻿using System.Threading.Tasks;
using System.Xml;

using SharedLib;

namespace Core;

public sealed class WApi
{
    private string BaseUrl { get; }

    private string BaseUIMapUrl { get; }

    public WApi(StartupClientVersion scv)
    {
        BaseUrl = scv.Version switch
        {
            ClientVersion.SoM => "https://classic.wowhead.com",
            ClientVersion.TBC => "https://tbc.wowhead.com",
            ClientVersion.Wrath => "https://www.wowhead.com/wotlk",
            _ => "https://www.wowhead.com",
        };

        BaseUIMapUrl = scv.Version switch
        {
            ClientVersion.SoM => "https://wow.zamimg.com/images/wow/classic/maps/enus/original/",
            ClientVersion.TBC => "https://wow.zamimg.com/images/wow/tbc/maps/enus/original/",
            ClientVersion.Wrath => "https://wow.zamimg.com/images/wow/wrath/maps/enus/original/",
            _ => "https://wow.zamimg.com/images/wow/maps/enus/original/",
        };

    }

    public string NpcId => $"{BaseUrl}/npc=";
    public string ItemId => $"{BaseUrl}/item=";
    public string SpellId => $"{BaseUrl}/spell=";

    public const string TinyIconUrl = "https://wow.zamimg.com/images/wow/icons/tiny/{0}.gif";
    public const string MedIconUrl = "https://wow.zamimg.com/images/wow/icons/medium/{0}.jpg";

    private static readonly XmlReaderSettings iconSettings = new() { Async = true, LineNumberOffset = 14 };
    private const string ICON = "icon";

    public async Task<string> FetchItemIconName(int itemId)
    {
        try
        {
            using XmlReader xml = XmlReader.Create($"{ItemId}{itemId}&xml", iconSettings);
            while (await xml.ReadAsync())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.Name.Contains(ICON))
                {
                    return await xml.ReadElementContentAsStringAsync();
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public string GetMapImage(int areaId)
    {
        return $"{BaseUIMapUrl}{areaId}.jpg";
    }
}