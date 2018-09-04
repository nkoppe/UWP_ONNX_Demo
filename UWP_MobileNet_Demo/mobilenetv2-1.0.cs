using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.AI.MachineLearning;
namespace UWP_MobileNet_Demo
{
    
    public sealed class Input
    {
        public TensorFloat data; // shape(1,3,224,224)
    }
    
    public sealed class Output
    {
        public TensorFloat mobilenetv20_output_flatten0_reshape0; // shape(1,1000)
    }
    
    public sealed class Model
    {
        private LearningModel model;
        private LearningModelSession session;
        private LearningModelBinding binding;
        public static async Task<Model> CreateFromStreamAsync(IRandomAccessStreamReference stream)
        {
            Model learningModel = new Model();
            learningModel.model = await LearningModel.LoadFromStreamAsync(stream);
            learningModel.session = new LearningModelSession(learningModel.model);
            learningModel.binding = new LearningModelBinding(learningModel.session);
            return learningModel;
        }
        public async Task<Output> EvaluateAsync(Input input)
        {
            binding.Bind("data", input.data);
            var result = await session.EvaluateAsync(binding, "0");
            var output = new Output();
            output.mobilenetv20_output_flatten0_reshape0 = result.Outputs["mobilenetv20_output_flatten0_reshape0"] as TensorFloat;
            return output;
        }
    }
}
