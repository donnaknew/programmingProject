/*
  HomeOSArduinoUnoExample. 
  
  Demonstrates how a HomeOS Arduino Uno device should respond to queries from the scout and drivers
  Leverages the Blink example to turns on an LED on for one second, then off for one second, repeatedly.
 
  This example code is in the public domain.
  
  All commands sent should have the form [<command>].   Code below expects '[' as start indicator and ']' as terminator of command
  Standard commands 
  [?] - send unique name of device using this format: HomeOSArduinoDevice_NameofDriver_YourOrganization_UniqueID   NameofDriver and Your Organization must match your Lab of Things driver name
  
  Use '[!' at beginning to tell driver you are sending error message.  For example [!Badly formatted command].
  
  Your driver should define commands to send and receive from Arduino. Most straightforward if each command starts with different character
 */
 
// Pin 13 has an LED connected on most Arduino boards.
// give it a name:
int led = 13;
int numCharRead = 0;
char incomingData[20];
int dummyValue = 0;

const int buttonPin = 2;    // the input pin reading the button pushing event
int ledState = HIGH;         // the current state of the output pin
int buttonState;             // the current reading from the input pin
int lastButtonState = LOW;   // the previous reading from the input pin
// the following variables are long's because the time, measured in miliseconds,
// will quickly become a bigger number than can be stored in an int.
long lastDebounceTime = 0;  // the last time the output pin was toggled
long debounceDelay = 50;    // the debounce time; increase if the output flickers
int isPushed = 0;

// the setup routine runs once when you press reset:
void setup() {                
  // initialize the digital pin as an output.
  pinMode(led, OUTPUT);  
  pinMode(buttonPin, INPUT);
  digitalWrite(led, ledState);
  //Setup the serial port
 Serial.begin(9600);
}

// the loop routine runs over and over again forever:
void loop() {
  if (Serial.available() > 0) {
     numCharRead = Serial.readBytesUntil(']',  incomingData, 19);
     //buffer should contain some command with '[' at start and then command - it will not have terminator   
      processCommandsFromLoT(numCharRead); 
      dummyValue = 0;      
  }  

/////////////// button pushing //////////////////////////////  
  // read the state of the switch into a local variable:
  int reading = digitalRead(buttonPin);
  // check to see if you just pressed the button 
  // (i.e. the input went from LOW to HIGH),  and you've waited 
  // long enough since the last press to ignore any noise:  

  // If the switch changed, due to noise or pressing:
  if (reading != lastButtonState) {
    // reset the debouncing timer
    lastDebounceTime = millis();
  } 

  if ((millis() - lastDebounceTime) > debounceDelay) {
    if (reading != buttonState) {
      buttonState = reading;
      
      if (buttonState == HIGH) {
        if (isPushed == 0) {
	  ledState = !ledState;
          dummyValue = 101; // StartRecording
          isPushed = 1;
        }
        else if (isPushed == 1) {
          ledState = !ledState;
          dummyValue = 100; // StopRecording
          isPushed = 0;
        }
      }
    }
  }
  
  // set the LED:
  // digitalWrite(led, ledState);

  // save the reading.  Next time through the loop,
  // it'll be the lastButtonState:
  lastButtonState = reading;
  
}
int processCommandsFromLoT(int numCharRead ) {
  
   if (numCharRead < 2)  { //need at least '[' and one other character
       Serial.print("[!Badly formatted command]");  //! to indicate text string
       return -1; //skip the rest
     }
  
    //everything after '[' to the length read is the command.
    if (incomingData[0] != '[') {
       Serial.print("[!Badly formatted command]");  //! to indicate text string
       return -1; //skip the rest
    }
  
    //switch based on first character after the '['
      switch(incomingData[1]) {
        
        case '?':
         Serial.print("[HomeOSArduinoDevice_Dummy_MicrosoftResearch_1234]");
        break;
        
        ///ADD COMMANDS RELEVANT TO YOUR DEVICE & DRIVER HERE
        //example for dummy
        case 'v':
         Serial.print('[');
         Serial.print(dummyValue);
         Serial.print(']');
         

         break;
        
        default:
          Serial.print("[!No matching command]");
      }
}

