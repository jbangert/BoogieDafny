using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie.ModelViewer;
using System.IO;
using Microsoft.Boogie;
namespace EmacsBVD
{
    class Program
    {
        static void Visit(IEnumerable<IDisplayNode> foo, int level )
        {  
            foreach(var node in foo){
                          Console.WriteLine(" {3}{0} {1} = {2}",node.Name, node.ToolTip,node.Value, new String('*',level));
                          Visit(node.Children, level + 1);
            }
        }
        static void Main(string[] args)
        {
            if(args.Length <1){
                return;
            }
            Model[] allmodels ;
            ILanguageProvider provider = Microsoft.Boogie.ModelViewer.Dafny.Provider.Instance;
            string modelfilename = args[0];
            using (var rd = File.OpenText(modelfilename)){
                allmodels = Model.ParseModels(rd).ToArray();
            }
            int i=0;
            foreach (var model in allmodels){
                 i++;
                 ViewOptions vopts = new ViewOptions(); 
		         var langmodel = provider.GetLanguageSpecificModel(model,vopts);
               //  Console.WriteLine("*Model {0}",i);
                 var states = langmodel.States.ToArray();
                 foreach (IState state in states) { 
                     Console.WriteLine("*State {0}", state.Name);
                     Visit(state.Nodes, 2);
                 }

            }
            

        }
    }
}
