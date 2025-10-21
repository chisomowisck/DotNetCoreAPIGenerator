#nullable enable
using System.IO;                 
using Scriban;
using Scriban.Runtime;

namespace Db2Crud.Generation;

internal sealed class TemplateRenderer
{
    public string Render(Template tpl, object model)
    {
        var ctx = new TemplateContext
        {
            MemberRenamer   = m => m.Name,
            StrictVariables = true
        };
        var globals = new ScriptObject();
        globals.Import(model, renamer: m => m.Name);
        ctx.PushGlobal(globals);
        return tpl.Render(ctx);
    }

    public Template Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Template file not found: {path}");
        var text = File.ReadAllText(path);
        return Template.Parse(text, path); // pass path for better error messages
    }
}
